// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    /// <summary>
    /// An exception which indicates multiple matches in action selection.
    /// </summary>
    public class AmbiguousActionException : InvalidOperationException
    {
        public AmbiguousActionException(string message)
            : base(message)
        {
        }
    }
}
