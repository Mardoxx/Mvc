// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.AspNetCore.Mvc.Razor.Internal
{
#pragma warning disable CS0618
    /// <summary>
    /// A type that uses Roslyn to compile C# content.
    /// </summary>
    public class DefaultRoslynCompilationService : ICompilationService
    {
        private readonly RazorCompiler _razorCompiler;

        /// <summary>
        /// Initalizes a new instance of the <see cref="DefaultRoslynCompilationService"/> class.
        /// </summary>
        /// <param name="razorCompiler">The <see cref="RazorCompiler"/>.</param>
        public DefaultRoslynCompilationService(
            RazorCompiler razorCompiler)
        {
            _razorCompiler = razorCompiler;
        }

        /// <inheritdoc />
        public CompilationResult Compile(RazorCodeDocument codeDocument, RazorCSharpDocument cSharpDocument)
        {
            return _razorCompiler.Compile(codeDocument, cSharpDocument);
        }
    }
#pragma warning restore CS0618
}
