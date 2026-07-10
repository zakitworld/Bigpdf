namespace Bigpdf.Services;

public static class PageRangeParser
{
    public static bool TryParse(string input, out List<int> pages, out string? error)
    {
        pages = new List<int>();
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "No pages specified";
            return false;
        }

        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (rangeParts.Length != 2
                    || !int.TryParse(rangeParts[0], out var start)
                    || !int.TryParse(rangeParts[1], out var end))
                {
                    error = $"Invalid range: {part}";
                    return false;
                }

                if (start > end)
                {
                    error = $"Invalid range: {part}";
                    return false;
                }

                for (var i = start; i <= end; i++)
                    pages.Add(i);
            }
            else if (int.TryParse(part, out var page))
            {
                pages.Add(page);
            }
            else
            {
                error = $"Invalid page: {part}";
                return false;
            }
        }

        if (pages.Count == 0)
        {
            error = "No pages specified";
            return false;
        }

        return true;
    }
}
