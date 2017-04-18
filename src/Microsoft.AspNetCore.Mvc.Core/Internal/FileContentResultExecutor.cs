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
            RangeItemHeaderValue range = new RangeItemHeaderValue(0, 0);
            long rangeLength = default(long);
            if (result.EnableRangeProcessing)
            {
                range = SetContentRangeAndStatusCode(
                    context,
                    result.LastModified,
                    result.EntityTag,
                    result.FileContents.Length);

                rangeLength = (range == null) ? 0 : SetRangeHeaders(context, result, range);
            }

            SetHeadersAndLog(context, result);
            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static Task WriteFileAsync(ActionContext context, FileContentResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;

            if (!result.EnableRangeProcessing)
            {
                return response.Body.WriteAsync(result.FileContents, offset: 0, count: result.FileContents.Length);
            }

            else if (range == null || rangeLength == 0)
            {
                return Task.CompletedTask;
            }

            else
            {
                try
                {
                    return response.Body.WriteAsync(result.FileContents, offset: (int)range.From.Value, count: (int)rangeLength);
                }

                catch (OperationCanceledException ex)
                {
                    // Don't throw this exception, it's most likely caused by the client disconnecting.
                    // However, if it was cancelled for any other reason we need to prevent empty responses.
                    context.HttpContext.Abort();
                    return Task.FromException(ex);
                }

            }
        }
    }
}
