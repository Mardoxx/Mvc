// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO;
using System.Threading.Tasks;
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
            var (range, rangeLength, serveBody) = SetHeadersAndLog(
                context,
                result,
                result.FileContents.Length,
                result.LastModified,
                result.EntityTag);

            if (!serveBody)
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

            await WriteFileAsync(context.HttpContext, fileContentsStream, range, rangeLength);
        }
    }
}
