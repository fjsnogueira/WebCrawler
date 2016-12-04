﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Css;
using AngleSharp.Dom.Html;
using AngleSharp.Extensions;
using AngleSharp.Parser.Css;

namespace WebCrawler
{
    public class Crawler : IDisposable
    {
        private readonly ConcurrentQueue<DiscoveredUrl> _toProcess = new ConcurrentQueue<DiscoveredUrl>();
        public event EventHandler<DocumentEventArgs> DocumentParsed;
        public event EventHandler<DocumentEventArgs> DocumentUpdated;
        private readonly HttpClient _client;

        static Crawler()
        {
            Encoding.RegisterProvider(WebEncodingProvider.Instance);
        }

        public Crawler()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ClientCertificateOptions = ClientCertificateOption.Manual,
                CheckCertificateRevocationList = true,
            };

            _client = new HttpClient(handler, true);
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/54.0.2840.99 Safari/537.36");
        }

        public async Task<CrawlResult> RunAsync(string address, CancellationToken ct = default(CancellationToken))
        {
            var result = new CrawlResult();
            result.Address = address;

            _toProcess.Enqueue(new DiscoveredUrl { Url = address });
            DiscoveredUrl toBeProcessed;
            while (_toProcess.TryDequeue(out toBeProcessed))
            {
                await ProcessItem(result, toBeProcessed, ct);
            }

            return result;
        }

        private async Task ProcessItem(CrawlResult result, DiscoveredUrl toBeProcessed, CancellationToken ct)
        {
            // Test the domain, same domain as address or external by 1 level 
            if (!toBeProcessed.IsRedirect && !IsSameHost(result.Address, toBeProcessed.Url))
            {
                if (toBeProcessed.Document == null || !IsSameHost(result.Address, toBeProcessed.Document.Url))
                    return;
            }

            // Already processed
            var existingDocument = result.Documents.FirstOrDefault(d => d.Url == toBeProcessed.Url);
            if (existingDocument != null)
            {
                DocumentRef documentRef = new DocumentRef();
                documentRef.Document = toBeProcessed.Document;
                documentRef.Excerpt = toBeProcessed.Excerpt;
                existingDocument.ReferencedBy.Add(documentRef);
                OnDocumentUpdated(existingDocument);
                return;
            }

            var doc = await GetAsync(toBeProcessed.Url, ct).ConfigureAwait(false);
            if (toBeProcessed.Document != null)
            {
                doc.ReferencedBy.Add(new DocumentRef { Document = toBeProcessed.Document, Excerpt = toBeProcessed.Excerpt });
            }

            result.Documents.Add(doc);
            OnDocumentParsed(doc);
        }

        private async Task<Document> GetAsync(string address, CancellationToken ct = default(CancellationToken))
        {
            var doc = new Document();
            doc.CrawledOn = DateTime.UtcNow;
            doc.Url = address;
            try
            {
                using (var response = await _client.GetAsync(address, ct).ConfigureAwait(false))
                {
                    doc.StatusCode = response.StatusCode;
                    doc.Headers = Combine(Clone(response.Headers), Clone(response.Content?.Headers));

                    if (IsRedirect(response.StatusCode))
                    {
                        if (response.Headers.TryGetValues("Location", out var locationHeader))
                        {
                            var location = locationHeader.FirstOrDefault();
                            doc.RedirectUrl = new Url(new Url(address), location).Href;
                            EnqueueRedirect(doc, doc.RedirectUrl);
                        }
                    }
                    else
                    {
                        if (response.Content != null)
                        {
                            var contentType = response.Content?.Headers.ContentType?.MediaType;
                            if (contentType == null || IsHtmlMimeType(contentType))
                            {
                                await HandleHtml(doc, response, ct);
                            }
                            else if (IsCssMimeType(contentType))
                            {
                                await HandleCss(doc, response, ct);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                doc.ErrorMessage = GetErrorMessage(ex);
                doc.FullErrorMessage = ex.ToString();
            }

            return doc;
        }

        public string GetErrorMessage(Exception ex)
        {
            string message = ex.Message;
            string s = ex.GetType().FullName + ": " + message;

            if (ex.InnerException != null)
            {
                s += Environment.NewLine + " ---> " + GetErrorMessage(ex.InnerException);
            }

            return s;
        }

        private async Task HandleHtml(Document document, HttpResponseMessage response, CancellationToken ct)
        {
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var config = Configuration.Default
                .WithDefaultLoader()
                .WithLocaleBasedEncoding();

            var browsingContext = BrowsingContext.New(config);
            var htmlDocument = await browsingContext.OpenAsync(action => action.Address(document.Url).Content(content).Headers(response.Headers).Status(response.StatusCode), ct).ConfigureAwait(false);

            document.Title = htmlDocument.Title;

            foreach (var node in htmlDocument.All)
            {
                // TODO sourceset
                if (node is IHtmlAnchorElement anchorElement)
                {
                    Enqueue(document, anchorElement.Href, node);
                }
                else if (node is IHtmlScriptElement scriptElement)
                {
                    if (!string.IsNullOrEmpty(scriptElement.Source))
                    {
                        if (string.IsNullOrEmpty(scriptElement.Type) || IsJavaScriptMimeType(scriptElement.Type))
                        {
                            var href = new Url(scriptElement.BaseUrl, scriptElement.Source).Href;
                            Enqueue(document, href, node);
                        }
                    }
                }
                else if (node is IHtmlLinkElement linkElement)
                {
                    Enqueue(document, linkElement.Href, node);
                }
                else if (node is IHtmlImageElement imageElement)
                {
                    Enqueue(document, imageElement.Source, node);
                }
                else if (node is IHtmlSourceElement sourceElement)
                {
                    Enqueue(document, sourceElement.Source, node);
                }
                else if (node is IHtmlTrackElement trackElement)
                {
                    Enqueue(document, trackElement.Source, node);
                }
                else if (node is IHtmlObjectElement objectElement)
                {
                    Enqueue(document, objectElement.Source, node);
                }
                else if (node is IHtmlAudioElement audioElement)
                {
                    Enqueue(document, audioElement.Source, node);
                }
                else if (node is IHtmlVideoElement videoElement)
                {
                    Enqueue(document, videoElement.Source, node);
                    Enqueue(document, videoElement.Poster, node);
                }
                else if (node is IHtmlInlineFrameElement frameElement)
                {
                    Enqueue(document, frameElement.Source, node);
                }
                else if (node is IHtmlAreaElement areaElement)
                {
                    Enqueue(document, areaElement.Href, node);
                }
                else if (node is IHtmlStyleElement styleElement)
                {
                    HandleCss(document, styleElement.InnerHtml);
                }

                var style = node.GetAttribute("style");
                if (!string.IsNullOrWhiteSpace(style))
                {
                    HandleCss(document, "dummy{" + style + "}");
                }
            }
        }

        private async Task HandleCss(Document document, HttpResponseMessage response, CancellationToken ct)
        {
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var cssParser = new CssParser();
            var stylesheet = cssParser.ParseStylesheet(content);
            HandleCss(document, stylesheet);
        }

        private void HandleCss(Document document, string css)
        {
            var cssParser = new CssParser();
            var stylesheet = cssParser.ParseStylesheet(css);
            HandleCss(document, stylesheet);
        }

        private void HandleCss(Document document, ICssNode node)
        {
            if (node is ICssProperty propertyRule)
            {
                string value = propertyRule.Value;
                if (value != null)
                {
                    var parts = value.Split(',');
                    foreach (var part in parts)
                    {
                        var url = ParseCssUrl(part);
                        if (url != null)
                        {
                            var finalUrl = new Url(new Url(document.Url), url);
                            Enqueue(document, finalUrl.Href, propertyRule);
                        }
                    }
                }
            }

            foreach (var child in node.Children)
            {
                HandleCss(document, child);
            }
        }

        private string ParseCssUrl(string value)
        {
            if (value == null)
                return null;

            value = value.Trim();
            if (value.StartsWith("url"))
            {
                var result = new StringBuilder(value.Length);
                bool isInParentheses = false;
                bool isInQuote = false;
                char quoteChar = '\0';
                for (int i = 3; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == ' ' && !isInQuote)
                        continue;

                    if (c == '(')
                    {
                        if (isInQuote)
                        {
                            result.Append(c);
                            continue;
                        }

                        if (isInParentheses)
                            return null;

                        isInParentheses = true;
                        continue;
                    }
                    else if (c == ')')
                    {
                        if (isInQuote)
                        {
                            result.Append(c);
                            continue;
                        }

                        if (!isInParentheses)
                        {
                            return null;
                        }

                        return result.ToString();
                    }
                    else if (c == '"' || c == '\'')
                    {
                        if (isInQuote)
                        {
                            if (c == quoteChar)
                            {
                                isInQuote = false;
                                continue;
                            }
                            else
                            {
                                result.Append(c);
                                continue;
                            }
                        }

                        isInQuote = true;
                        quoteChar = c;
                        result.Clear();
                    }
                    else
                    {
                        if (isInParentheses)
                        {
                            result.Append(c);
                        }
                    }
                }
            }

            return null;
        }

        private void Enqueue(Document document, string url, IElement node)
        {
            if (!MustProcessUrl(url))
                return;

            // Remove fragment
            var parsedUrl = new Url(url);
            parsedUrl.Fragment = null;

            _toProcess.Enqueue(new DiscoveredUrl { Url = parsedUrl.Href, Document = document, Excerpt = GetExcerpt(node) });
        }

        private void Enqueue(Document document, string url, ICssNode node)
        {
            if (!MustProcessUrl(url))
                return;

            // Remove fragment
            var parsedUrl = new Url(url);
            parsedUrl.Fragment = null;

            _toProcess.Enqueue(new DiscoveredUrl { Url = parsedUrl.Href, Document = document, Excerpt = GetExcerpt(node) });
        }

        private void EnqueueRedirect(Document document, string url)
        {
            if (!MustProcessUrl(url))
                return;

            // Remove fragment
            var parsedUrl = new Url(url);
            parsedUrl.Fragment = null;

            _toProcess.Enqueue(new DiscoveredUrl { Url = parsedUrl.Href, Document = document, IsRedirect = true });
        }

        private bool MustProcessUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string GetExcerpt(IElement node)
        {
            if (node == null)
                return null;

            return node.OuterHtml;
        }

        private static string GetExcerpt(ICssNode node)
        {
            if (node == null)
                return null;

            return node.ToCss();
        }

        protected virtual void OnDocumentParsed(Document document)
        {
            DocumentParsed?.Invoke(this, new DocumentEventArgs(document));
        }

        protected virtual void OnDocumentUpdated(Document document)
        {
            DocumentUpdated?.Invoke(this, new DocumentEventArgs(document));
        }

        private static bool IsSameHost(string a, string b)
        {
            return new Url(a).Host == new Url(b).Host;
        }

        private static bool IsHtmlMimeType(string mimeType)
        {
            string[] mimeTypes =
            {
                "text/html",
                "application/xhtml+xml"
            };

            foreach (var type in mimeTypes)
            {
                if (string.Equals(mimeType, type, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsCssMimeType(string mimeType)
        {
            string[] mimeTypes =
            {
                "text/css"
            };

            foreach (var type in mimeTypes)
            {
                if (string.Equals(mimeType, type, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsJavaScriptMimeType(string mimeType)
        {
            string[] mimeTypes =
            {
                "application/ecmascript",
                "application/javascript",
                "application/x-ecmascript",
                "application/x-javascript",
                "text/ecmascript",
                "text/javascript",
                "text/javascript1.0",
                "text/javascript1.1",
                "text/javascript1.2",
                "text/javascript1.3",
                "text/javascript1.4",
                "text/javascript1.5",
                "text/jscript",
                "text/livescript",
                "text/x-ecmascript",
                "text/x-javascript"
            };

            foreach (var type in mimeTypes)
            {
                if (string.Equals(mimeType, type, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsRedirect(HttpStatusCode code)
        {
            return code == HttpStatusCode.Redirect || code == HttpStatusCode.MovedPermanently;
        }

        private static IDictionary<string, string> Clone(IEnumerable<KeyValuePair<string, IEnumerable<string>>> dict)
        {
            var clone = new Dictionary<string, string>();
            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    clone.Add(kvp.Key, string.Join(", ", kvp.Value));
                }
            }

            return clone;
        }

        private static IDictionary<string, string> Combine(params IDictionary<string, string>[] dicts)
        {
            IDictionary<string, string> dict = new Dictionary<string, string>();
            foreach (var dictionary in dicts)
            {
                foreach (var kvp in dictionary)
                {
                    dict.Add(kvp.Key, kvp.Value);
                }
            }

            return dict;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}