// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class FileResultExecutorBase
    {
        private PreconditionState _ifMatchState;
        private PreconditionState _ifNoneMatchState;
        private PreconditionState _ifModifiedSinceState;
        private PreconditionState _ifUnmodifiedSinceState;

        public FileResultExecutorBase(ILogger logger)
        {
            Logger = logger;
            _ifMatchState = PreconditionState.Unspecified;
            _ifNoneMatchState = PreconditionState.Unspecified;
            _ifModifiedSinceState = PreconditionState.Unspecified;
            _ifUnmodifiedSinceState = PreconditionState.Unspecified;
        }

        public enum PreconditionState
        {
            Unspecified,
            NotModified,
            ShouldProcess,
            PreconditionFailed,
        }

        protected ILogger Logger { get; }

        public PreconditionState GetPreconditionState()
        {
            return GetMaxPreconditionState(_ifMatchState, _ifNoneMatchState,
                _ifModifiedSinceState, _ifUnmodifiedSinceState);
        }

        private static PreconditionState GetMaxPreconditionState(params PreconditionState[] states)
        {
            PreconditionState max = PreconditionState.Unspecified;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] > max)
                {
                    max = states[i];
                }
            }
            return max;
        }

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
            var response = context.HttpContext.Response;
            response.Headers[HeaderNames.AcceptRanges] = "bytes";
            var method = context.HttpContext.Request.Method;
            var isGet = string.Equals("GET", method, StringComparison.OrdinalIgnoreCase);
            if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
            {
                result.EnableRangeProcessing = false;
            }

            if (result.EnableRangeProcessing && range != null)
            {
                long rangeLength = SetContentLength(context, range);
                return rangeLength;
            }

            return default(long);
        }

        protected RangeItemHeaderValue SetContentRangeAndStatusCode(
            ActionContext context,
            long fileLength,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var range = ParseRange(context, fileLength, lastModified, etag);
            var response = context.HttpContext.Response;
            var httpResponseHeaders = response.GetTypedHeaders();
            bool rangeNotSatisfiable = false;
            if (range == null)
            {
                rangeNotSatisfiable = true;
            }

            httpResponseHeaders.LastModified = lastModified;
            httpResponseHeaders.ETag = etag;

            if (rangeNotSatisfiable)
            {
                // 14.16 Content-Range - A server sending a response with status code 416 (Requested range not satisfiable)
                // SHOULD include a Content-Range field with a byte-range-resp-spec of "*". The instance-length specifies
                // the current length of the selected resource.  e.g. */length
                httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(fileLength);
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.ContentLength = fileLength;
                return null;
            }

            httpResponseHeaders.ContentRange = new ContentRangeHeaderValue(
                range.From.Value,
                range.To.Value,
                fileLength);

            response.StatusCode = StatusCodes.Status206PartialContent;
            return range;
        }

        private RangeItemHeaderValue ParseRange(
            ActionContext context,
            long fileLength,
            DateTimeOffset? lastModified = null,
            EntityTagHeaderValue etag = null)
        {
            var httpContext = context.HttpContext;
            var httpRequestHeaders = httpContext.Request.GetTypedHeaders();
            var response = httpContext.Response;

            var range = RangeHelper.ParseRange(
                httpContext,
                httpRequestHeaders,
                lastModified,
                etag);

            if (range != null)
            {
                var normalizedRanges = RangeHelper.NormalizeRanges(range, fileLength);
                return normalizedRanges.Single();
            }

            else
            {
                return null;
            }
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

        protected void ComputeIfMatch(ActionContext context, FileResult result, DateTimeOffset lastModified, EntityTagHeaderValue etag)
        {
            // 14.24 If-Match
            var httpContext = context.HttpContext;
            var httpRequestHeaders = httpContext.Request.GetTypedHeaders();
            var ifMatch = httpRequestHeaders.IfMatch;
            if (ifMatch != null && ifMatch.Any())
            {
                _ifMatchState = PreconditionState.PreconditionFailed;
                foreach (var entityTag in ifMatch)
                {
                    if (etag.Equals(EntityTagHeaderValue.Any) || etag.Compare(etag, useStrongComparison: true))
                    {
                        _ifMatchState = PreconditionState.ShouldProcess;
                        break;
                    }
                }
            }

            // 14.26 If-None-Match
            var ifNoneMatch = httpRequestHeaders.IfNoneMatch;
            if (ifNoneMatch != null && ifNoneMatch.Any())
            {
                _ifNoneMatchState = PreconditionState.ShouldProcess;
                foreach (var entityTag in ifNoneMatch)
                {
                    if (etag.Equals(EntityTagHeaderValue.Any) || etag.Compare(etag, useStrongComparison: true))
                    {
                        _ifNoneMatchState = PreconditionState.NotModified;
                        break;
                    }
                }
            }
        }

        protected void ComputeIfModifiedSince(ActionContext context, DateTimeOffset lastModified)
        {
            var now = DateTimeOffset.UtcNow;
            var httpContext = context.HttpContext;
            var httpRequestHeaders = httpContext.Request.GetTypedHeaders();

            // 14.25 If-Modified-Since
            var ifModifiedSince = httpRequestHeaders.IfModifiedSince;
            if (ifModifiedSince.HasValue && ifModifiedSince <= now)
            {
                bool modified = ifModifiedSince < lastModified;
                _ifModifiedSinceState = modified ? PreconditionState.ShouldProcess : PreconditionState.NotModified;
            }

            // 14.28 If-Unmodified-Since
            var ifUnmodifiedSince = httpRequestHeaders.IfUnmodifiedSince;
            if (ifUnmodifiedSince.HasValue && ifUnmodifiedSince <= now)
            {
                bool unmodified = ifUnmodifiedSince >= lastModified;
                _ifUnmodifiedSinceState = unmodified ? PreconditionState.ShouldProcess : PreconditionState.PreconditionFailed;
            }
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
