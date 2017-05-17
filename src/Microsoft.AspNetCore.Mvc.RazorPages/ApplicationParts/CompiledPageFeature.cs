// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;

namespace Microsoft.AspNetCore.Mvc.ApplicationParts
{
    public class CompiledPageInfoFeature
    {
        public IList<RazorPageAttribute> CompiledPages { get; } = new List<RazorPageAttribute>();
    }
}
