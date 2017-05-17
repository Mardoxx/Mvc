// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Mvc.Razor.Internal
{
    /// <summary>
    /// Caches the result of runtime compilation of Razor files for the duration of the application lifetime.
    /// </summary>
    public class RazorViewCompiler : IViewCompiler
    {
        private readonly object _initializeLock = new object();
        private readonly object _cacheLock = new object();
        private readonly ConcurrentDictionary<string, string> _normalizedPathLookup;
        private readonly IFileProvider _fileProvider;
        private readonly RazorTemplateEngine _templateEngine;
        private readonly Action<RoslynCompilationContext> _compilationCallback;
        private readonly ILogger _logger;
        private readonly CSharpCompiler _csharpCompiler;
        private IMemoryCache _cache;

        public RazorViewCompiler(
            IFileProvider fileProvider,
            RazorTemplateEngine templateEngine,
            CSharpCompiler csharpCompiler,
            Action<RoslynCompilationContext> compilationCallback,
            IList<RazorViewAttribute> precompiledViews,
            ILogger logger)
        {
            if (fileProvider == null)
            {
                throw new ArgumentNullException(nameof(fileProvider));
            }

            if (templateEngine == null)
            {
                throw new ArgumentNullException(nameof(templateEngine));
            }

            if (csharpCompiler == null)
            {
                throw new ArgumentNullException(nameof(csharpCompiler));
            }

            if (compilationCallback == null)
            {
                throw new ArgumentNullException(nameof(compilationCallback));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _fileProvider = fileProvider;
            _templateEngine = templateEngine;
            _csharpCompiler = csharpCompiler;
            _compilationCallback = compilationCallback;
            _logger = logger;

            _normalizedPathLookup = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            _cache = new MemoryCache(new MemoryCacheOptions());

            foreach (var attribute in precompiledViews)
            {
                var path = GetNormalizedPath(attribute.Path);
                var value = new CompiledViewDescriptor
                {
                    RelativePath = path,
                    ViewAttribute = attribute,
                    IsPrecompiled = true,
                };
                _cache.Set(path, value, new MemoryCacheEntryOptions { Priority = CacheItemPriority.NeverRemove });
            }
        }

        /// <inheritdoc />
        public Task<CompiledViewDescriptor> CompileAsync(string relativePath)
        {
            if (relativePath == null)
            {
                throw new ArgumentNullException(nameof(relativePath));
            }

            // Attempt to lookup the cache entry using the passed in path. This will succeed if the path is already
            // normalized and a cache entry exists.
            if (!_cache.TryGetValue(relativePath, out Task<CompiledViewDescriptor> viewDescriptor))
            {
                var normalizedPath = GetNormalizedPath(relativePath);
                if (!_cache.TryGetValue(normalizedPath, out viewDescriptor))
                {
                    viewDescriptor = CreateCacheEntry(normalizedPath);
                }
            }

            // The Task does not represent async work and is meant to provide per-entry locking.
            // Hence it is ok to perform .GetResult() to read the result.
            return viewDescriptor;
        }

        private Task<CompiledViewDescriptor> CreateCacheEntry(string normalizedPath)
        {
            TaskCompletionSource<CompiledViewDescriptor> compilationTaskSource = null;
            MemoryCacheEntryOptions cacheEntryOptions;
            Task<CompiledViewDescriptor> cacheEntry;

            // Safe races cannot be allowed when compiling Razor pages. To ensure only one compilation request succeeds
            // per file, we'll lock the creation of a cache entry. Creating the cache entry should be very quick. The
            // actual work for compiling files happens outside the critical section.
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(normalizedPath, out cacheEntry))
                {
                    return cacheEntry;
                }

                cacheEntryOptions = new MemoryCacheEntryOptions();

                cacheEntryOptions.ExpirationTokens.Add(_fileProvider.Watch(normalizedPath));
                var projectItem = _templateEngine.Project.GetItem(normalizedPath);
                if (!projectItem.Exists)
                {
                    var notFoundDescriptorResult = new CompiledViewDescriptor
                    {
                        RelativePath = normalizedPath,
                        ExpirationTokens = cacheEntryOptions.ExpirationTokens,
                    };
                    return Task.FromResult(notFoundDescriptorResult);
                }
                else
                {
                    // A file exists and needs to be compiled.
                    compilationTaskSource = new TaskCompletionSource<CompiledViewDescriptor>();
                    foreach (var importItem in _templateEngine.GetImportItems(projectItem))
                    {
                        cacheEntryOptions.ExpirationTokens.Add(_fileProvider.Watch(importItem.Path));
                    }
                    cacheEntry = compilationTaskSource.Task;
                }

                cacheEntry = _cache.Set(normalizedPath, cacheEntry, cacheEntryOptions);
            }

            if (compilationTaskSource != null)
            {
                // Indicates that a file was found and needs to be compiled.
                Debug.Assert(cacheEntryOptions != null);

                try
                {
                    var generatedAssembly = CompileAndEmit(normalizedPath);
                    var descriptor = ReadDescriptor(normalizedPath, generatedAssembly, cacheEntryOptions.ExpirationTokens);
                    compilationTaskSource.SetResult(descriptor);
                }
                catch (Exception ex)
                {
                    compilationTaskSource.SetException(ex);
                }
            }

            return cacheEntry;
        }

        private CompiledViewDescriptor ReadDescriptor(string path, Assembly generatedAssembly, IList<IChangeToken> changeTokens)
        {
            var exportedType = generatedAssembly.GetExportedTypes().FirstOrDefault(f => !f.IsNested);
            return new CompiledViewDescriptor
            {
                ViewAttribute = new RazorViewAttribute(path, exportedType),
                RelativePath = path,
                ExpirationTokens = changeTokens,
            };
        }

        private Assembly CompileAndEmit(string relativePath)
        {
            var codeDocument = _templateEngine.CreateCodeDocument(relativePath);
            var cSharpDocument = _templateEngine.GenerateCode(codeDocument);

            if (cSharpDocument.Diagnostics.Count > 0)
            {
                throw CompilationFailedResultFactory.Create(
                    codeDocument,
                    cSharpDocument.Diagnostics);
            }

            return CompileAndEmit(codeDocument, cSharpDocument);
        }

        internal Assembly CompileAndEmit(RazorCodeDocument codeDocument, RazorCSharpDocument cSharpDocument)
        {
            _logger.GeneratedCodeToAssemblyCompilationStart(codeDocument.Source.FileName);

            var startTimestamp = _logger.IsEnabled(LogLevel.Debug) ? Stopwatch.GetTimestamp() : 0;

            var assemblyName = Path.GetRandomFileName();
            var compilation = CreateCompilation(cSharpDocument.GeneratedCode, assemblyName);

            using (var assemblyStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var result = compilation.Emit(
                    assemblyStream,
                    pdbStream,
                    options: _csharpCompiler.EmitOptions);

                if (!result.Success)
                {
                    throw CompilationFailedResultFactory.Create(
                        codeDocument,
                        cSharpDocument.GeneratedCode,
                        assemblyName,
                        result.Diagnostics);
                }

                assemblyStream.Seek(0, SeekOrigin.Begin);
                pdbStream.Seek(0, SeekOrigin.Begin);

                var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream, pdbStream);
                _logger.GeneratedCodeToAssemblyCompilationEnd(codeDocument.Source.FileName, startTimestamp);

                return assembly;
            }
        }

        private CSharpCompilation CreateCompilation(string compilationContent, string assemblyName)
        {
            var sourceText = SourceText.From(compilationContent, Encoding.UTF8);
            var syntaxTree = _csharpCompiler.CreateSyntaxTree(sourceText).WithFilePath(assemblyName);
            var compilation = _csharpCompiler
                .CreateCompilation(assemblyName)
                .AddSyntaxTrees(syntaxTree);
            compilation = ExpressionRewriter.Rewrite(compilation);

            var compilationContext = new RoslynCompilationContext(compilation);
            _compilationCallback(compilationContext);
            compilation = compilationContext.Compilation;
            return compilation;
        }

        private string GetNormalizedPath(string relativePath)
        {
            Debug.Assert(relativePath != null);
            if (relativePath.Length == 0)
            {
                return relativePath;
            }

            if (!_normalizedPathLookup.TryGetValue(relativePath, out var normalizedPath))
            {
                var builder = new StringBuilder(relativePath);
                builder.Replace('\\', '/');
                if (builder[0] != '/')
                {
                    builder.Insert(0, '/');
                }
                normalizedPath = builder.ToString();
                _normalizedPathLookup.TryAdd(relativePath, normalizedPath);
            }

            return normalizedPath;
        }
    }
}
