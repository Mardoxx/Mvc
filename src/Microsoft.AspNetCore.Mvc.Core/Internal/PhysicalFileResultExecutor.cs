// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class PhysicalFileResultExecutor : FileResultExecutorBase
    {
        private const int DefaultBufferSize = 0x1000;

        public PhysicalFileResultExecutor(ILoggerFactory loggerFactory)
            : base(CreateLogger<PhysicalFileResultExecutor>(loggerFactory))
        {
        }

        public Task ExecuteAsync(ActionContext context, PhysicalFileResult result)
        {
            var rangeInfo = new(RangeItemHeaderValue range, long rangeLength)?();

            var fileInfo = GetFileInfo(result.FileName);
            rangeInfo = SetHeadersAndLog(
                context,
                result,
                fileInfo.Length,
                fileInfo.LastModified);

            if (rangeInfo.HasValue)
            {
                return WriteFileAsync(context, result, rangeInfo.Value.range, rangeInfo.Value.rangeLength);
            }

            return WriteFileAsync(context, result, null, 0);
        }

        private async Task WriteFileAsync(ActionContext context, PhysicalFileResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var fileStream = GetFileStream(result.FileName);

            if (!Path.IsPathRooted(result.FileName))
            {
                throw new NotSupportedException(Resources.FormatFileResult_PathNotRooted(result.FileName));
            }

            var sendFile = response.HttpContext.Features.Get<IHttpSendFileFeature>();
            if (sendFile != null)
            {
                await sendFile.SendFileAsync(
                    result.FileName,
                    offset: 0,
                    count: null,
                    cancellation: default(CancellationToken));
            }
            else if (range == null || !fileStream.CanSeek)
            {
                using (fileStream)
                {
                    await fileStream.CopyToAsync(response.Body, DefaultBufferSize);
                }
            }
            else if (rangeLength == 0)
            {
                return;
            }
            else
            {
                try
                {
                    fileStream.Seek(range.From.Value, SeekOrigin.Begin);
                    await StreamCopyOperation.CopyToAsync(fileStream, response.Body, rangeLength, context.HttpContext.RequestAborted);
                }

                catch (OperationCanceledException)
                {
                    // Don't throw this exception, it's most likely caused by the client disconnecting.
                    // However, if it was cancelled for any other reason we need to prevent empty responses.
                    context.HttpContext.Abort();
                }
            }
        }

        protected virtual Stream GetFileStream(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    DefaultBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        protected virtual FileInfo GetFileInfo(string path)
        {
            var fileInfo = new System.IO.FileInfo(path);
            return new FileInfo
            {
                Length = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
            };
        }

        protected class FileInfo
        {
            public long Length { get; set; }

            public DateTimeOffset LastModified { get; set; }
        }
    }
}
