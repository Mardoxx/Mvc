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
    public class FileStreamResultExecutor : FileResultExecutorBase
    {
        // default buffer size as defined in BufferedStream type
        private const int BufferSize = 0x1000;

        public FileStreamResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<VirtualFileResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, FileStreamResult result)
        {
            long? fileLength = null;
            RangeItemHeaderValue range;
            long rangeLength;
            bool returnEmptyBody;
            if (result.FileStream.CanSeek)
            {
                fileLength = result.FileStream.Length;
            }

            (range, rangeLength, returnEmptyBody) = SetHeadersAndLog(
                context,
                result,
                fileLength,
                result.LastModified,
                result.EntityTag);

            if (returnEmptyBody)
            {
                return Task.CompletedTask;
            }

            return WriteFileAsync(context, result, range, rangeLength);
        }

        private static async Task WriteFileAsync(ActionContext context, FileStreamResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var outputStream = response.Body;
            if (range != null && rangeLength == 0)
            {
                return;
            }

            using (result.FileStream)
            {
                if (range == null)
                {
                    try
                    {
                        result.FileStream.Seek(0, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(result.FileStream, outputStream, null, BufferSize, context.HttpContext.RequestAborted);
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
                        result.FileStream.Seek(range.From.Value, SeekOrigin.Begin);
                        await StreamCopyOperation.CopyToAsync(result.FileStream, outputStream, rangeLength, BufferSize, context.HttpContext.RequestAborted);
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
