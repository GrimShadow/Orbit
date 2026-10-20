using System.IO.Compression;
using System.Net;
using System.Text;
using Dam.Domain.Knowledge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Parser.Parts;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Dam.Infrastructure.Knowledge;

/// <summary>What reading a file's table of contents found.</summary>
/// <param name="Chapters">In reading order, at most 500.</param>
/// <param name="Source">pdf or html, or none when the file has no usable bookmarks or headings.</param>
/// <param name="Truncated">There were more than 500 entries; the rest were left out.</param>
public sealed record ChapterReading(IReadOnlyList<Chapter> Chapters, string Source, bool Truncated, int? PageCount = null)
{
    public static readonly ChapterReading Empty = new([], ChapterSources.None, false);
}

/// <summary>
/// Reads a PDF's outline (its bookmarks) into chapters (ADR-0039 decision 7): title, nesting level and first page; each chapter's last page is derived from the
/// next chapter of the same or a higher level. Pure and defensive: a damaged file, no outline, a bookmark that points nowhere, a named destination that cannot
/// be resolved, an outline that loops, or a huge one all give a result (possibly an empty one), never an exception or an endless walk.
/// </summary>
/// <remarks>
/// PdfPig's own bookmark reader drops any bookmark it cannot turn into a page, together with everything beneath it, so a heading that points nowhere would take
/// its sections with it. The outline is therefore walked here, on the document's tokens: a heading with no usable destination stays (taking the first page of its
/// sections), and PdfPig's reader is used only as a fallback when this walk finds nothing.
/// </remarks>
public static class PdfOutlineReader
{
    /// <summary>The most outline items looked at, whether or not they become chapters, so a hostile or enormous outline costs a bounded amount of work.</summary>
    private const int MaxItems = 20_000;
    private const int MaxPageNodes = 200_000;
    private const int MaxNamedEntries = 100_000;

    public static ChapterReading Read(byte[] pdf)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var pages = doc.NumberOfPages;
            if (pages == 0) return ChapterReading.Empty;

            var raw = WalkOutline(doc, pages, out var truncated);
            if (raw.Count == 0 && doc.TryGetBookmarks(out var bookmarks) && bookmarks.Roots.Count > 0) raw = Flatten(bookmarks.Roots, pages, out truncated);
            if (raw.Count == 0) return new ChapterReading([], ChapterSources.None, false, pages);

            var tidy = ChapterRules.Tidy(raw, out var cut);
            var withPages = ChapterRules.WithPageRanges(InheritPages(tidy), pages);
            return new ChapterReading(withPages, withPages.Count == 0 ? ChapterSources.None : ChapterSources.Pdf, truncated || cut, pages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Encrypted, truncated or otherwise unreadable: no chapters, and the document stays usable.
            System.Diagnostics.Debug.WriteLine($"PDF outline unreadable: {ex.GetType().Name}");
            return ChapterReading.Empty;
        }
    }

    /// <summary>A heading with no page of its own opens where its first section does (a part title that only groups chapters).</summary>
    private static List<Chapter> InheritPages(List<Chapter> chapters)
    {
        var result = new List<Chapter>(chapters);
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].PageFrom is not null) continue;
            for (var j = i + 1; j < result.Count && result[j].Level > result[i].Level; j++)
                if (result[j].PageFrom is { } page) { result[i] = result[i] with { PageFrom = page }; break; }
        }
        return result;
    }

    // ---- the walk on the document's own tokens ------------------------------------------------------------------

    private static List<Chapter> WalkOutline(PdfDocument doc, int pageCount, out bool truncated)
    {
        truncated = false;
        var result = new List<Chapter>();
        var scanner = doc.Structure.TokenScanner;
        var catalog = doc.Structure.Catalog.CatalogDictionary;
        if (!TryDict(catalog, "Outlines", scanner, out var outlines) || !outlines.Data.TryGetValue("First", out var first)) return result;

        var pages = new Lazy<Dictionary<IndirectReference, int>>(() => PageNumbers(catalog, scanner));
        var named = new Lazy<Dictionary<string, IToken>>(() => NamedDestinations(catalog, scanner));

        var visited = new HashSet<IndirectReference>();
        var stack = new Stack<(IToken Item, int Depth)>();
        stack.Push((first, 1));
        var items = 0;
        while (stack.Count > 0)
        {
            var (token, depth) = stack.Pop();
            if (++items > MaxItems) { truncated = true; break; }
            if (token is IndirectReferenceToken r && !visited.Add(r.Data)) continue;       // reached twice: the outline loops back on itself
            if (!DirectObjectFinder.TryGet<DictionaryToken>(token, scanner, out var item)) continue;

            var nextDepth = depth;
            var title = ChapterRules.CleanTitle(TextOf(item, "Title", scanner));
            var kind = Destination(item, scanner, out var destination);
            if (kind != DestinationKind.External && title.Length > 0)
            {
                if (result.Count >= KnowledgeLimits.MaxChapters) { truncated = true; break; }
                var page = destination is null ? null : PageOf(destination, scanner, pages, named, pageCount, 0);
                result.Add(new Chapter(title, null, Math.Min(depth, KnowledgeLimits.MaxChapterLevel), page, null));
                nextDepth = depth + 1;                                                    // only a real chapter deepens the nesting
            }
            if (item.Data.TryGetValue("Next", out var next)) stack.Push((next, depth));
            if (item.Data.TryGetValue("First", out var child)) stack.Push((child, nextDepth));
        }
        return result;
    }

    private enum DestinationKind { None, Local, External }

    /// <summary>Where a bookmark goes: its /Dest, or the /D of a GoTo action. A GoToR, Launch or URI action opens something other than this file.</summary>
    private static DestinationKind Destination(DictionaryToken item, IPdfTokenScanner scanner, out IToken? destination)
    {
        destination = null;
        if (item.Data.TryGetValue("Dest", out var dest)) { destination = dest; return DestinationKind.Local; }
        if (!TryDict(item, "A", scanner, out var action)) return DestinationKind.None;
        var type = DirectObjectFinder.TryGet<NameToken>(action.Data.GetValueOrDefault("S") ?? NullToken.Instance, scanner, out var s) ? s.Data : "";
        if (type == "GoTo") { if (action.Data.TryGetValue("D", out var d)) destination = d; return DestinationKind.Local; }
        return DestinationKind.External;
    }

    private static string TextOf(DictionaryToken d, string key, IPdfTokenScanner scanner)
    {
        if (!d.Data.TryGetValue(key, out var t)) return "";
        if (DirectObjectFinder.TryGet<StringToken>(t, scanner, out var s)) return s.Data;
        return DirectObjectFinder.TryGet<HexToken>(t, scanner, out var h) ? h.Data : "";
    }

    private static bool TryDict(DictionaryToken from, string key, IPdfTokenScanner scanner, out DictionaryToken result)
    {
        result = DictionaryToken.Empty;
        if (!from.Data.TryGetValue(key, out var t) || !DirectObjectFinder.TryGet<DictionaryToken>(t, scanner, out var found)) return false;
        result = found;
        return true;
    }

    /// <summary>The page a destination opens, or null: an explicit destination names a page object, a name or string is looked up (in /Dests, or the /Names tree), and anything unresolvable has no page.</summary>
    private static int? PageOf(IToken destination, IPdfTokenScanner scanner, Lazy<Dictionary<IndirectReference, int>> pages, Lazy<Dictionary<string, IToken>> named, int pageCount, int depth)
    {
        if (depth > 5) return null;
        switch (destination)
        {
            case IndirectReferenceToken r:
                {
                    var resolved = scanner.Get(r.Data)?.Data;
                    return resolved is null || resolved is IndirectReferenceToken ? null : PageOf(resolved, scanner, pages, named, pageCount, depth + 1);
                }
            case ArrayToken a when a.Length > 0:
                {
                    if (a[0] is IndirectReferenceToken pageRef && pages.Value.TryGetValue(pageRef.Data, out var number) && number >= 1 && number <= pageCount) return number;
                    return null;
                }
            case NameToken n:
                return named.Value.TryGetValue(n.Data, out var byName) ? PageOf(byName, scanner, pages, named, pageCount, depth + 1) : null;
            case StringToken s:
                return named.Value.TryGetValue(s.Data, out var byString) ? PageOf(byString, scanner, pages, named, pageCount, depth + 1) : null;
            case HexToken h:
                return named.Value.TryGetValue(h.Data, out var byHex) ? PageOf(byHex, scanner, pages, named, pageCount, depth + 1) : null;
            case DictionaryToken d when d.Data.TryGetValue("D", out var inner):
                return PageOf(inner, scanner, pages, named, pageCount, depth + 1);
            default:
                return null;
        }
    }

    /// <summary>Page object reference to 1-based page number, from the page tree in reading order.</summary>
    private static Dictionary<IndirectReference, int> PageNumbers(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        var numbers = new Dictionary<IndirectReference, int>();
        if (!catalog.Data.TryGetValue("Pages", out var root)) return numbers;
        var visited = new HashSet<IndirectReference>();
        var stack = new Stack<IToken>();
        stack.Push(root);
        var count = 0;
        var nodes = 0;
        while (stack.Count > 0 && ++nodes <= MaxPageNodes)
        {
            var token = stack.Pop();
            IndirectReference? reference = null;
            if (token is IndirectReferenceToken r) { if (!visited.Add(r.Data)) continue; reference = r.Data; }
            if (!DirectObjectFinder.TryGet<DictionaryToken>(token, scanner, out var node)) continue;
            if (node.Data.TryGetValue("Kids", out var kids) && DirectObjectFinder.TryGet<ArrayToken>(kids, scanner, out var list))
            {
                for (var i = list.Length - 1; i >= 0; i--) stack.Push(list[i]);
            }
            else
            {
                count++;
                if (reference is { } page) numbers[page] = count;
            }
        }
        return numbers;
    }

    /// <summary>Named destinations: the catalog's /Dests dictionary (old style) and the /Names /Dests name tree, as name to destination token.</summary>
    private static Dictionary<string, IToken> NamedDestinations(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        var map = new Dictionary<string, IToken>(StringComparer.Ordinal);
        if (TryDict(catalog, "Dests", scanner, out var legacy))
            foreach (var (key, value) in legacy.Data.Take(MaxNamedEntries)) map[key] = value;

        if (TryDict(catalog, "Names", scanner, out var names) && names.Data.TryGetValue("Dests", out var treeRoot))
        {
            var visited = new HashSet<IndirectReference>();
            var stack = new Stack<IToken>();
            stack.Push(treeRoot);
            var nodes = 0;
            while (stack.Count > 0 && ++nodes <= MaxNamedEntries && map.Count < MaxNamedEntries)
            {
                var token = stack.Pop();
                if (token is IndirectReferenceToken r && !visited.Add(r.Data)) continue;
                if (!DirectObjectFinder.TryGet<DictionaryToken>(token, scanner, out var node)) continue;
                if (node.Data.TryGetValue("Names", out var pairs) && DirectObjectFinder.TryGet<ArrayToken>(pairs, scanner, out var array))
                    for (var i = 0; i + 1 < array.Length; i += 2)
                    {
                        string? key = DirectObjectFinder.TryGet<StringToken>(array[i], scanner, out var ks) ? ks.Data
                            : DirectObjectFinder.TryGet<HexToken>(array[i], scanner, out var kh) ? kh.Data : null;
                        if (key is not null) map[key] = array[i + 1];
                    }
                if (node.Data.TryGetValue("Kids", out var kids) && DirectObjectFinder.TryGet<ArrayToken>(kids, scanner, out var kidList))
                    for (var i = kidList.Length - 1; i >= 0; i--) stack.Push(kidList[i]);
            }
        }
        return map;
    }

    // ---- PdfPig's own reader, used only when the walk above finds nothing ----------------------------------------

    /// <summary>Walks PdfPig's bookmarks depth first without recursion, so a very deep outline cannot overflow the stack, and stops at the chapter limit.</summary>
    private static List<Chapter> Flatten(IReadOnlyList<BookmarkNode> roots, int pageCount, out bool truncated)
    {
        var result = new List<Chapter>();
        truncated = false;
        var visited = new HashSet<BookmarkNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(BookmarkNode Node, int Depth)>();
        for (var i = roots.Count - 1; i >= 0; i--) stack.Push((roots[i], 1));
        var steps = 0;
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (!visited.Add(node)) continue;
            if (++steps > KnowledgeLimits.MaxChapters * 20) { truncated = true; break; }

            var title = ChapterRules.CleanTitle(node.Title);
            var isLink = node is not (ExternalBookmarkNode or EmbeddedBookmarkNode or UriBookmarkNode);
            var page = node is DocumentBookmarkNode d && d.PageNumber >= 1 && d.PageNumber <= pageCount ? d.PageNumber : (int?)null;
            if (isLink && title.Length > 0)
            {
                if (result.Count >= KnowledgeLimits.MaxChapters) { truncated = true; break; }
                result.Add(new Chapter(title, null, Math.Min(depth, KnowledgeLimits.MaxChapterLevel), page, null));
            }
            var level = isLink && title.Length > 0 ? depth + 1 : depth;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], level));
        }
        return result;
    }
}

/// <summary>
/// Reads the headings (h1 to h3) of an HTML document into chapters: the heading's text, its level and its <c>id</c> as the anchor (a generated slug when it has
/// none). A tolerant scanner, not a browser: it never fetches anything and never runs a script; comments, scripts and styles are skipped, and entities decoded.
/// </summary>
public static class HtmlHeadingReader
{
    public const int MaxHtmlChars = 5 * 1024 * 1024;

    public static ChapterReading Read(string html)
    {
        if (html.Length > MaxHtmlChars) html = html[..MaxHtmlChars];
        var found = new List<(string Text, int Level, string? Id)>();
        var truncated = false;
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= html.Length) break;
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            var (name, closing, tagEnd) = ReadTag(html, lt);
            if (tagEnd < 0) break;
            i = tagEnd;
            if (closing || name.Length == 0) continue;

            if (name is "script" or "style" or "textarea" or "title")
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
                continue;
            }
            if (name.Length == 2 && name[0] == 'h' && name[1] is '1' or '2' or '3')
            {
                var level = name[1] - '0';
                var attrs = html.AsSpan(lt, tagEnd - lt).ToString();
                var (text, after) = ReadInnerText(html, tagEnd, name);
                i = after;
                var clean = ChapterRules.CleanTitle(WebUtility.HtmlDecode(text));
                if (clean.Length == 0) continue;
                if (found.Count >= KnowledgeLimits.MaxChapters) { truncated = true; break; }
                found.Add((clean, level, AttributeValue(attrs, "id")));
            }
        }

        if (found.Count == 0) return ChapterReading.Empty;
        var used = new HashSet<string>(found.Select(f => f.Id).Where(id => id is not null)!, StringComparer.Ordinal);
        var chapters = new List<Chapter>(found.Count);
        foreach (var (text, level, id) in found)
        {
            var anchor = id ?? UniqueSlug(text, used);
            chapters.Add(new Chapter(text, anchor, level, null, null));
        }
        return new ChapterReading(chapters, ChapterSources.Html, truncated);
    }

    /// <summary>Reads the tag that starts at <paramref name="lt"/>: its lower-cased name, whether it closes, and where it ends (after the '>'), or -1 when the text ends first.</summary>
    private static (string Name, bool Closing, int End) ReadTag(string html, int lt)
    {
        var i = lt + 1;
        var closing = false;
        if (i < html.Length && html[i] == '/') { closing = true; i++; }
        var nameStart = i;
        while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] == '-')) i++;
        var name = html[nameStart..i].ToLowerInvariant();
        if (name.Length == 0) return ("", false, lt + 1);          // a '<' that is just text ("a < b"), not a tag
        // Skip to the '>' that ends the tag, not counting one inside a quoted attribute value.
        char quote = '\0';
        for (; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') return (name, closing, i + 1);
        }
        return ("", closing, -1);
    }

    /// <summary>The text of a heading up to its closing tag, with nested tags (and any script or style inside) dropped. Stops early at a closing tag of another heading level, which a sloppy page may use.</summary>
    private static (string Text, int After) ReadInnerText(string html, int start, string headingName)
    {
        var sb = new StringBuilder();
        var i = start;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) { sb.Append(html, i, html.Length - i); return (sb.ToString(), html.Length); }
            sb.Append(html, i, lt - i);
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            var (name, closing, tagEnd) = ReadTag(html, lt);
            if (tagEnd < 0) return (sb.ToString(), html.Length);
            if (name.Length == 0) { sb.Append('<'); i = tagEnd; continue; }       // a '<' that is just text
            if (closing && name == headingName) return (sb.ToString(), tagEnd);
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6' && !closing) return (sb.ToString(), lt);   // an unclosed heading: the next one starts here
            if (!closing && name is "script" or "style")
            {
                var close = html.IndexOf("</" + name, tagEnd, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
                continue;
            }
            if (name is "br" or "p" or "div" or "li") sb.Append(' ');
            i = tagEnd;
        }
        return (sb.ToString(), html.Length);
    }

    /// <summary>The value of an attribute in a start tag's text, or null.</summary>
    public static string? AttributeValue(string tag, string attribute)
    {
        var i = tag.IndexOf(' ', StringComparison.Ordinal);
        while (i >= 0 && i < tag.Length)
        {
            while (i < tag.Length && (char.IsWhiteSpace(tag[i]) || tag[i] == '/')) i++;
            var nameStart = i;
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] is not ('=' or '>' or '/')) i++;
            var name = tag[nameStart..i];
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            string? value = null;
            if (i < tag.Length && tag[i] == '=')
            {
                i++;
                while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
                if (i < tag.Length && tag[i] is '"' or '\'')
                {
                    var q = tag[i++];
                    var end = tag.IndexOf(q, i);
                    if (end < 0) return null;
                    value = tag[i..end];
                    i = end + 1;
                }
                else
                {
                    var start = i;
                    while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '>') i++;
                    value = tag[start..i];
                }
            }
            if (name.Length == 0) break;
            if (string.Equals(name, attribute, StringComparison.OrdinalIgnoreCase))
            {
                var v = WebUtility.HtmlDecode(value ?? "").Trim();
                return v.Length is > 0 and <= KnowledgeLimits.MaxAnchor ? v : null;
            }
        }
        return null;
    }

    /// <summary>A URL-safe slug of a heading: lower case, words joined with '-', a number appended when it repeats one already used.</summary>
    internal static string UniqueSlug(string text, HashSet<string> used)
    {
        var sb = new StringBuilder();
        var dash = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch)) { if (dash && sb.Length > 0) sb.Append('-'); sb.Append(char.ToLowerInvariant(ch)); dash = false; }
            else dash = true;
        }
        var baseSlug = sb.Length == 0 ? "section" : sb.ToString();
        if (baseSlug.Length > 80) baseSlug = baseSlug[..80].TrimEnd('-');
        var slug = baseSlug;
        for (var n = 2; !used.Add(slug); n++) slug = baseSlug + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return slug;
    }
}

/// <summary>
/// Finds and reads the entry page of an HTML5 bundle (a zip). It never extracts anything to disk, so a hostile entry name ("../x") cannot escape anywhere; such
/// entries are simply not candidates. Limits: how many entries are looked at, the largest page read, and a hard cap on the bytes actually decompressed (an entry's
/// declared size can lie).
/// </summary>
public static class BundleEntryReader
{
    public const int MaxEntries = 10_000;
    public const int MaxPageBytes = 5 * 1024 * 1024;

    /// <summary>The entry page's HTML and its path inside the zip, or nulls when the archive has none, is too large to trust, or cannot be read.</summary>
    public static (string? Html, string? Entry) ReadEntryPage(Stream zip)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntries) return (null, null);
            var candidates = new List<(string Path, ZipArchiveEntry Entry)>();
            foreach (var e in archive.Entries)
            {
                var path = SafePath(e.FullName);
                if (path is null) continue;
                if (path.EndsWith('/')) continue;
                if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) candidates.Add((path, e));
            }
            var chosen = Choose(candidates.Select(c => c.Path).ToList());
            if (chosen is null) return (null, null);
            var entry = candidates.First(c => c.Path == chosen).Entry;
            if (entry.Length > MaxPageBytes) return (null, null);

            using var s = entry.Open();
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + read > MaxPageBytes) return (null, null);      // it decompresses to more than it said
                ms.Write(buffer, 0, read);
            }
            return (DecodeText(ms.ToArray()), chosen);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>The entry's path with '/' separators, or null when it could escape the bundle (absolute, drive letters, '..' segments, control characters).</summary>
    public static string? SafePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512) return null;
        if (name.Any(char.IsControl) || name.Contains('\\', StringComparison.Ordinal) || name.StartsWith('/') || (name.Length > 1 && name[1] == ':')) return null;
        var segments = name.Split('/');
        return segments.Any(s => s == "..") ? null : name;
    }

    /// <summary>index.html at the top; else the only top-level folder's index.html, else the shallowest index.html; else the only HTML file at the shallowest depth; else none.</summary>
    public static string? Choose(IReadOnlyList<string> htmlPaths)
    {
        if (htmlPaths.Count == 0) return null;
        static int Depth(string p) => p.Count(c => c == '/');
        static bool IsIndex(string p) => p[(p.LastIndexOf('/') + 1)..].Equals("index.html", StringComparison.OrdinalIgnoreCase) || p[(p.LastIndexOf('/') + 1)..].Equals("index.htm", StringComparison.OrdinalIgnoreCase);

        var indexes = htmlPaths.Where(IsIndex).OrderBy(Depth).ThenBy(p => p, StringComparer.Ordinal).ToList();
        if (indexes.Count > 0) return indexes[0];
        var shallowest = htmlPaths.Min(Depth);
        var top = htmlPaths.Where(p => Depth(p) == shallowest).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return top.Count == 1 ? top[0] : null;
    }

    /// <summary>UTF-8 (with or without a byte-order mark), UTF-16 with a byte-order mark, else Latin-1.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
