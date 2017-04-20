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
            long rangeLength = default(long);

            var range = new RangeItemHeaderValue(0, 0);
            if (result.EnableRangeProcessing)
            {
                if (result.LastModified.HasValue)
                {
                    ComputeIfMatch(context, result, result.LastModified.Value, result.EntityTag);
                    ComputeIfModifiedSince(context, result.LastModified.Value);
                    range = SetContentRangeAndStatusCode(
                        context,
                        result.FileContents.Length,
                        result.LastModified.Value,
                        result.EntityTag);
                }

                else
                {
                    range = SetContentRangeAndStatusCode(context, result.FileContents.Length);
                }

                rangeLength = SetRangeHeaders(context, result, range);
            }

            SetHeadersAndLog(context, result);
            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static Task WriteFileAsync(ActionContext context, FileContentResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;

            if (!result.EnableRangeProcessing || range == null)
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
