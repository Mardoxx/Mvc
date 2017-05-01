// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileContentResultExecutor : FileResultExecutorBase
    {
        // default buffer size as defined in BufferedStream type
        private const int BufferSize = 0x1000;

        public FileContentResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<FileContentResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, FileContentResult result)
        {
            RangeItemHeaderValue range;
            long rangeLength;
            bool returnEmptyBody;

            (range, rangeLength, returnEmptyBody) = SetHeadersAndLog(
                context,
                result,
                result.FileContents.Length,
                result.LastModified,
                result.EntityTag);

            if (returnEmptyBody)
            {
                return Task.CompletedTask;
            }

            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static async Task WriteFileAsync(ActionContext context, FileContentResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;
            var fileContentsStream = new MemoryStream(result.FileContents);
            if (range != null && rangeLength == 0)
            {
                return;
            }

            using (fileContentsStream)
            {
                if (range == null)
                {
                    try
                    {
                        fileContentsStream.Seek(0, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(fileContentsStream, outputStream, null, BufferSize, context.HttpContext.RequestAborted);
                    }

                    catch (OperationCanceledException)
                    {
                        // Don't throw this exception, it's most likely caused by the client disconnecting.
                        // However, if it was cancelled for any other reason we need to prevent empty responses.
                        context.HttpContext.Abort();
                    }
                }

                else
                {
                    try
                    {
                        fileContentsStream.Seek(range.From.Value, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(fileContentsStream, outputStream, rangeLength, BufferSize, context.HttpContext.RequestAborted);
                    }

                    catch (OperationCanceledException)
                    {
                        // Don't throw this exception, it's most likely caused by the client disconnecting.
                        // However, if it was cancelled for any other reason we need to prevent empty responses.
                        context.HttpContext.Abort();
                    }
                }
            }
        }
    }
}
