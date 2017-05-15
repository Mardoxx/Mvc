﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class VirtualFileResultExecutor : FileResultExecutorBase
    {
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
            if (fileInfo.Exists)
            {
                var lastModified = result.LastModified ?? fileInfo.LastModified;
                var (range, rangeLength, serveBody) = SetHeadersAndLog(
                    context,
                    result,
                    fileInfo.Length,
                    lastModified,
                    result.EntityTag);
                if (serveBody)
                {
                    return WriteFileAsync(context, result, fileInfo, range, rangeLength);
                }
            }
            else
            {
                throw new FileNotFoundException(
                    Resources.FormatFileResult_InvalidPath(result.FileName), result.FileName);
            }

            return Task.CompletedTask;
        }

        private Task WriteFileAsync(ActionContext context, VirtualFileResult result, IFileInfo fileInfo, RangeItemHeaderValue range, long rangeLength)
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
                    return Task.CompletedTask;
                }
                var physicalPath = fileInfo.PhysicalPath;
                var sendFile = response.HttpContext.Features.Get<IHttpSendFileFeature>();
                if (sendFile != null && !string.IsNullOrEmpty(physicalPath))
                {
                    if (range != null)
                    {
                        return sendFile.SendFileAsync(
                            physicalPath,
                            offset: range.From ?? 0L,
                            count: rangeLength,
                            cancellation: default(CancellationToken));
                    }
                    else
                    {
                        return sendFile.SendFileAsync(
                            physicalPath,
                            offset: 0,
                            count: null,
                            cancellation: default(CancellationToken));
                    }
                }
                else
                {
                    return WriteFileAsync(context.HttpContext, GetFileStream(fileInfo), range, rangeLength);
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
