// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.Razor.Internal
{
    public class RazorCompiler
    {
        private readonly ICompilerCacheProvider _compilerCacheProvider;
        private readonly RazorTemplateEngine _templateEngine;
        private readonly Func<string, CompilationResult> _getCompilationResult;
        private readonly ILogger<RazorCompiler> _logger;
        private readonly CSharpCompiler _compiler;
        private readonly Action<RoslynCompilationContext> _compilationCallback;

        public RazorCompiler(
            ICompilerCacheProvider compilerCacheProvider,
            RazorTemplateEngine templateEngine,
            CSharpCompiler compiler,
            IOptions<RazorViewEngineOptions> optionsAccessor,
            ILoggerFactory logger)
        {
            _compilerCacheProvider = compilerCacheProvider;
            _templateEngine = templateEngine;
            _compiler = compiler;
            _compilationCallback = optionsAccessor.Value.CompilationCallback;
            _logger = logger.CreateLogger<RazorCompiler>();

            _getCompilationResult = GetCompilationResult;
        }

        private ICompilerCache CompilerCache => _compilerCacheProvider.Cache;

        public CompilerCacheResult Compile(string relativePath)
        {
            return CompilerCache.GetOrAdd(relativePath, _getCompilationResult);
        }

        public CompilationResult GetCompilationResult(string relativePath)
        {
            var codeDocument = _templateEngine.CreateCodeDocument(relativePath);
            var cSharpDocument = _templateEngine.GenerateCode(codeDocument);

            CompilationResult compilationResult;
            if (cSharpDocument.Diagnostics.Count > 0)
            {
                compilationResult = CompilationFailedResultFactory.Create(
                    codeDocument,
                    cSharpDocument.Diagnostics);
            }
            else
            {
                compilationResult = Compile(codeDocument, cSharpDocument);
            }

            return compilationResult;
        }

        internal CompilationResult Compile(RazorCodeDocument codeDocument, RazorCSharpDocument cSharpDocument)
        {
            _logger.GeneratedCodeToAssemblyCompilationStart(codeDocument.Source.FileName);

            var startTimestamp = _logger.IsEnabled(LogLevel.Debug) ? Stopwatch.GetTimestamp() : 0;

            var assemblyName = Path.GetRandomFileName();
            var compilation = CreateCompilation(cSharpDocument.GeneratedCode, assemblyName);

            using (var assemblyStream = new MemoryStream())
            {
                using (var pdbStream = new MemoryStream())
                {
                    var result = compilation.Emit(
                        assemblyStream,
                        pdbStream,
                        options: _compiler.EmitOptions);

                    if (!result.Success)
                    {
                        return CompilationFailedResultFactory.Create(
                            codeDocument,
                            cSharpDocument.GeneratedCode,
                            assemblyName,
                            result.Diagnostics);
                    }

                    assemblyStream.Seek(0, SeekOrigin.Begin);
                    pdbStream.Seek(0, SeekOrigin.Begin);

                    var assembly = LoadAssembly(assemblyStream, pdbStream);
                    var type = assembly.GetExportedTypes().FirstOrDefault(a => !a.IsNested);

                    _logger.GeneratedCodeToAssemblyCompilationEnd(codeDocument.Source.FileName, startTimestamp);

                    return new CompilationResult(type);
                }
            }
        }

        private CSharpCompilation CreateCompilation(string compilationContent, string assemblyName)
        {
            var sourceText = SourceText.From(compilationContent, Encoding.UTF8);
            var syntaxTree = _compiler.CreateSyntaxTree(sourceText).WithFilePath(assemblyName);
            var compilation = _compiler
                .CreateCompilation(assemblyName)
                .AddSyntaxTrees(syntaxTree);
            compilation = ExpressionRewriter.Rewrite(compilation);

            var compilationContext = new RoslynCompilationContext(compilation);
            _compilationCallback(compilationContext);
            compilation = compilationContext.Compilation;
            return compilation;
        }

        public static Assembly LoadAssembly(MemoryStream assemblyStream, MemoryStream pdbStream)
        {
            var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream, pdbStream);
            return assembly;
        }
    }
}
