// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Core;
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
            RangeItemHeaderValue range;
            long rangeLength;
            bool returnEmptyBody;
            var fileInfo = GetFileInfo(result.FileName);
            if (fileInfo.Exists)
            {
                var lastModified = result.LastModified == null ? fileInfo.LastModified : result.LastModified;
                (range, rangeLength, returnEmptyBody) = SetHeadersAndLog(
                    context,
                    result,
                    fileInfo.Length,
                    lastModified,
                    result.EntityTag);
            }
            else
            {
                (range, rangeLength, returnEmptyBody) = SetHeadersAndLog(context, result, null, result.LastModified, result.EntityTag);
            }

            if (returnEmptyBody)
            {
                return Task.CompletedTask;
            }

            return WriteFileAsync(context, result, range, rangeLength);
        }

        private async Task WriteFileAsync(ActionContext context, PhysicalFileResult result, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            if (!Path.IsPathRooted(result.FileName))
            {
                throw new NotSupportedException(Resources.FormatFileResult_PathNotRooted(result.FileName));
            }
            if (range != null && rangeLength == 0)
            {
                return;
            }
            var sendFile = response.HttpContext.Features.Get<IHttpSendFileFeature>();
            if (sendFile != null)
            {
                if (range != null)
                {
                    await sendFile.SendFileAsync(
                        result.FileName,
                        offset: (range.From.HasValue) ? range.From.Value : 0L,
                        count: rangeLength,
                        cancellation: default(CancellationToken));
                }
                else
                {
                    await sendFile.SendFileAsync(
                        result.FileName,
                        offset: 0,
                        count: null,
                        cancellation: default(CancellationToken));
                }
            }
            else
            {
                using (var fileStream = GetFileStream(result.FileName))
                {
                    if (range == null || !fileStream.CanSeek)
                    {
                        try
                        {
                            fileStream.Seek(0, SeekOrigin.Begin);
                            await StreamCopyOperation.CopyToAsync(fileStream, response.Body, null, DefaultBufferSize, context.HttpContext.RequestAborted);
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
                            fileStream.Seek(range.From.Value, SeekOrigin.Begin);
                            await StreamCopyOperation.CopyToAsync(fileStream, response.Body, rangeLength, DefaultBufferSize, context.HttpContext.RequestAborted);
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
                Exists = fileInfo.Exists,
                Length = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
            };
        }

        protected class FileInfo
        {
            public bool Exists { get; set; }

            public long Length { get; set; }

            public DateTimeOffset LastModified { get; set; }
        }
    }
}
