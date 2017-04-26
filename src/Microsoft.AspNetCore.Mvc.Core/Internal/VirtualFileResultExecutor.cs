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
    public class VirtualFileResultExecutor : FileResultExecutorBase
    {
        private const int DefaultBufferSize = 0x1000;
        private readonly IHostingEnvironment _hostingEnvironment;

        public VirtualFileResultExecutor(ILoggerFactory loggerFactory, IHostingEnvironment hostingEnvironment)
            : base(CreateLogger<VirtualFileResultExecutor>(loggerFactory))
        {
            if (hostingEnvironment == null)
            {
                throw new ArgumentNullException(nameof(hostingEnvironment));
            }

            _hostingEnvironment = hostingEnvironment;
        }

        public Task ExecuteAsync(ActionContext context, VirtualFileResult result)
        {
            var fileInfo = GetFileInformation(result);

            var rangeInfo = SetHeadersAndLog(
                context,
                result,
                fileInfo.Length,
                fileInfo.LastModified);

            if (rangeInfo.HasValue)
            {
                return WriteFileAsync(context, result, fileInfo, rangeInfo.Value.range, rangeInfo.Value.rangeLength);
            }

            return WriteFileAsync(context, result, fileInfo, null, 0);
        }

        private async Task WriteFileAsync(ActionContext context, VirtualFileResult result, IFileInfo fileInfo, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            var fileStream = GetFileStream(fileInfo);
            if (fileInfo.Exists)
            {
                var physicalPath = fileInfo.PhysicalPath;
                var sendFile = response.HttpContext.Features.Get<IHttpSendFileFeature>();
                if (sendFile != null && !string.IsNullOrEmpty(physicalPath))
                {
                    await sendFile.SendFileAsync(
                        physicalPath,
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
            else
            {
                throw new FileNotFoundException(
                    Resources.FormatFileResult_InvalidPath(result.FileName), result.FileName);
            }
        }

        private IFileInfo GetFileInformation(VirtualFileResult result)
        {
            var fileProvider = GetFileProvider(result);

            var normalizedPath = result.FileName;
            if (normalizedPath.StartsWith("~", StringComparison.Ordinal))
            {
                normalizedPath = normalizedPath.Substring(1);
            }

            var fileInfo = fileProvider.GetFileInfo(normalizedPath);
            return fileInfo;
        }

        private IFileProvider GetFileProvider(VirtualFileResult result)
        {
            if (result.FileProvider != null)
            {
                return result.FileProvider;
            }

            result.FileProvider = _hostingEnvironment.WebRootFileProvider;

            return result.FileProvider;
        }

        protected virtual Stream GetFileStream(IFileInfo fileInfo)
        {
            return fileInfo.CreateReadStream();
        }
    }
}
