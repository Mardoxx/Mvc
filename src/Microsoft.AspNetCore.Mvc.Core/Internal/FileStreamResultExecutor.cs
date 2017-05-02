// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
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
            if (result.FileStream.CanSeek)
            {
                fileLength = result.FileStream.Length;
            }

            var (range, rangeLength, serveBody) = SetHeadersAndLog(
                context,
                result,
                fileLength,
                result.LastModified,
                result.EntityTag);

            if (!serveBody)
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
            await WriteFileAsync(context.HttpContext, result.FileStream, range, rangeLength);
        }
    }
}
