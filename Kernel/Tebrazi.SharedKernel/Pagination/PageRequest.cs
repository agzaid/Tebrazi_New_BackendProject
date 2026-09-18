namespace Tebrazi.SharedKernel.Pagination;

/// <summary>
/// Port of <c>parsePagination</c> from <c>server/src/lib/queryHelpers.js</c>:
///
/// <code>
/// const page  = Math.max(1, parseInt(query.page) || 1);
/// const limit = Math.min(Math.max(1, parseInt(query.limit) || 20), 100);
/// </code>
///
/// The <c>||</c> is load-bearing and is NOT the same as a null-coalesce. <c>parseInt</c> returns
/// <c>0</c> for "0" and <c>NaN</c> for "abc", and both are falsy, so both fall through to the
/// default. That is why <c>?limit=0</c> yields 20 rather than 1, while <c>?limit=-5</c> yields 1
/// (it parses to a truthy -5, then <c>Math.max</c> floors it). Reproducing this with
/// <c>int?</c> plus <c>??</c> would give <c>?limit=0</c> a limit of 0 and return an empty page.
///
/// <c>parseInt</c> also parses a numeric PREFIX, so "50abc" is 50. Model binding a query string
/// to <c>int?</c> rejects that outright, which is why the parse here takes the raw string.
/// </summary>
public readonly record struct PageRequest(int Page, int Limit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public int Skip => (Page - 1) * Limit;

    /// <summary>
    /// Parses the raw <c>?page=</c> and <c>?limit=</c> strings exactly as the Node helper does.
    /// Bind these as <c>string?</c> on the controller action, not as <c>int?</c>.
    /// </summary>
    public static PageRequest Parse(string? page, string? limit)
    {
        var parsedPage = ParseIntPrefix(page);
        var parsedLimit = ParseIntPrefix(limit);

        // `|| 1` and `|| 20`: zero and unparseable both take the default.
        var effectivePage = parsedPage is null or 0 ? 1 : parsedPage.Value;
        var effectiveLimit = parsedLimit is null or 0 ? DefaultLimit : parsedLimit.Value;

        effectivePage = Math.Max(1, effectivePage);
        effectiveLimit = Math.Min(Math.Max(1, effectiveLimit), MaxLimit);

        return new PageRequest(effectivePage, effectiveLimit);
    }

    /// <summary>
    /// JavaScript's <c>parseInt</c>: leading whitespace, an optional sign, then as many digits as
    /// there are. "50abc" is 50, "abc" is null, "" is null. Overflow yields null rather than
    /// wrapping — Node would produce a float there, and no caller wants a page in the billions.
    /// </summary>
    private static int? ParseIntPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;

        var start = i;
        if (i < value.Length && (value[i] == '+' || value[i] == '-')) i++;

        var digitsStart = i;
        while (i < value.Length && char.IsAsciiDigit(value[i])) i++;

        if (i == digitsStart) return null;

        return int.TryParse(value.AsSpan(start, i - start), out var parsed) ? parsed : null;
    }
}

/// <summary>
/// The <c>pagination</c> object the Node list endpoints nest beside <c>data</c>. The key is
/// <c>limit</c>, not <c>pageSize</c> — the client reads it by that name.
/// </summary>
/// <param name="Pages">
/// <c>Math.ceil(total / limit)</c>, and 0 when <c>total</c> is 0.
/// </param>
public sealed record PaginationMeta(int Total, int Page, int Limit, int Pages)
{
    public static PaginationMeta From(int total, PageRequest request)
        => new(total, request.Page, request.Limit,
               total == 0 ? 0 : (int)Math.Ceiling(total / (double)request.Limit));

    /// <summary>
    /// The literal-zero variant used by the early returns: a physician with no profile row gets
    /// <c>pages: 0</c> alongside the PARSED page and limit, not a computed value.
    /// </summary>
    public static PaginationMeta Empty(PageRequest request)
        => new(0, request.Page, request.Limit, 0);
}
