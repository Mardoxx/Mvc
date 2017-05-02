// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
            var fileInfo = GetFileInfo(result.FileName);
            if (fileInfo.Exists)
            {
                var lastModified = result.LastModified == null ? fileInfo.LastModified : result.LastModified;
                var (range, rangeLength, serveBody) = SetHeadersAndLog(
                    context,
                    result,
                    fileInfo.Length,
                    lastModified,
                    result.EntityTag);
                if (serveBody)
                {
                    return WriteFileAsync(context, result, range, rangeLength);
                }
            }
            else
            {
                var (range, rangeLength, serveBody) = SetHeadersAndLog(context, result, null, result.LastModified, result.EntityTag);
                if (serveBody)
                {
                    return WriteFileAsync(context, result, range, rangeLength);
                }
            }

            return Task.CompletedTask;
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
                await WriteFileAsync(context.HttpContext, GetFileStream(result.FileName), range, rangeLength);
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
