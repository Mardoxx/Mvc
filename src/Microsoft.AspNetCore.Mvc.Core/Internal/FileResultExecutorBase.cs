// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileResultExecutorBase
    {
        public FileResultExecutorBase(ILogger logger)
        {
            Logger = logger;
        }

        protected ILogger Logger { get; }

        protected void SetHeadersAndLog(ActionContext context, FileResult result)
        {
            SetContentType(context, result);
            SetContentDispositionHeader(context, result);            
            Logger.FileResultExecuting(result.FileDownloadName);
        }

        private long SetContentLength(ActionContext context, RangeItemHeaderValue range)
        {
            long start = range.From.Value;
            long end = range.To.Value;
            long length = end - start + 1;
            var response = context.HttpContext.Response;
            response.ContentLength = length;
            return length;
        }

        protected long SetRangeHeaders(ActionContext context, FileResult result, RangeItemHeaderValue range)
        {
            var method = context.HttpContext.Request.Method;
            var isGet = string.Equals("GET", method, StringComparison.OrdinalIgnoreCase);
            if (!isGet)
            {
                result.EnableRangeProcessing = false;
            }

            if (result.EnableRangeProcessing)
            {
                var response = context.HttpContext.Response;
                response.Headers[HeaderNames.AcceptRanges] = "bytes";
                long rangeLength = SetContentLength(context, range);
                return rangeLength;
            }

            return default(long);
        }

        private void SetContentDispositionHeader(ActionContext context, FileResult result)
        {
            if (!string.IsNullOrEmpty(result.FileDownloadName))
            {
                // From RFC 2183, Sec. 2.3:
                // The sender may want to suggest a filename to be used if the entity is
                // detached and stored in a separate file. If the receiving MUA writes
                // the entity to a file, the suggested filename should be used as a
                // basis for the actual filename, where possible.
                var contentDisposition = new ContentDispositionHeaderValue("attachment");
                contentDisposition.SetHttpFileName(result.FileDownloadName);
                context.HttpContext.Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
            }
        }

        private void SetContentType(ActionContext context, FileResult result)
        {
            var response = context.HttpContext.Response;
            response.ContentType = result.ContentType;
        }

        protected static ILogger CreateLogger<T>(ILoggerFactory factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            return factory.CreateLogger<T>();
        }
    }
}
