// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileContentResultExecutor : FileResultExecutorBase
    {
        public FileContentResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<FileContentResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, FileContentResult result)
        {
            var rangeInfo = new(RangeItemHeaderValue range, long rangeLength)?();
            if (result.LastModified.HasValue)
            {
                rangeInfo = SetHeadersAndLog(
                    context,
                    result,
                    result.FileContents.Length,
                    result.LastModified.Value,
                    result.EntityTag);
            }
            else
            {
                rangeInfo = SetHeadersAndLog(
                    context,
                    result,
                    result.FileContents.Length);
            }

            if (rangeInfo.HasValue)
            {
                return WriteFileAsync(context, result, rangeInfo.Value.range, rangeInfo.Value.rangeLength);
            }

            return WriteFileAsync(context, result, null, 0);
        }

        private static Task WriteFileAsync(ActionContext context, FileContentResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;

            if (range == null)
            {
                return response.Body.WriteAsync(result.FileContents, offset: 0, count: result.FileContents.Length);
            }

            else if (rangeLength == 0)
            {
                return Task.CompletedTask;
            }

            else
            {
                return response.Body.WriteAsync(result.FileContents, offset: (int)range.From.Value, count: (int)rangeLength);
            }
        }
    }
}
