// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
            RangeItemHeaderValue range;
            long rangeLength;
            bool returnEmptyBody;
            (range, rangeLength, returnEmptyBody) = SetHeadersAndLog(
                context,
                result,
                fileInfo.Length,
                fileInfo.LastModified);

            if (returnEmptyBody)
            {
                return Task.CompletedTask;
            }

            return WriteFileAsync(context, result, fileInfo, range, rangeLength);
        }

        private async Task WriteFileAsync(ActionContext context, VirtualFileResult result, IFileInfo fileInfo, RangeItemHeaderValue range, long rangeLength)
        {
            var response = context.HttpContext.Response;
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    Resources.FormatFileResult_InvalidPath(result.FileName), result.FileName);
            }
            else
            {
                if (range != null && rangeLength == 0)
                {
                    return;
                }
                var physicalPath = fileInfo.PhysicalPath;
                var sendFile = response.HttpContext.Features.Get<IHttpSendFileFeature>();
                if (sendFile != null && !string.IsNullOrEmpty(physicalPath))
                {
                    if (range != null)
                    {
                        await sendFile.SendFileAsync(
                            physicalPath,
                            offset: (range.From.HasValue) ? range.From.Value : 0L,
                            count: rangeLength,
                            cancellation: default(CancellationToken));
                    }
                    else
                    {
                        await sendFile.SendFileAsync(
                            physicalPath,
                            offset: 0,
                            count: null,
                            cancellation: default(CancellationToken));
                    }
                }
                using (var fileStream = GetFileStream(fileInfo))
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
