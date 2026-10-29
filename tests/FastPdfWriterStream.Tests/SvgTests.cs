using System.Text;

namespace FastPdfWriterStream.Tests;

public class SvgTests
{
    private const string Sample = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
          <style>.a{fill:#2155da}</style>
          <rect class="a" x="10" y="10" width="50" height="30"/>
          <circle cx="100" cy="50" r="20" fill="#2155da"/>
          <g transform="translate(120,10)"><path class="a" d="M0,0h40v40H0zM5 5c10 0 20 10 30 30s-10 0-20-5q5 5 10 0t5 5Z" fill-rule="evenodd"/></g>
          <path d="M1 1" fill="none"/>
        </svg>
        """;

    [Fact]
    public void Svg_IsWrittenOnce_AndDrawnPerPage()
    {
        var svg = PdfSvg.Parse(Sample);
        Assert.Equal(200, svg.Width);
        Assert.Equal(PdfColor.FromHex("#2155DA"), svg.MonoColor);

        using var ms = new MemoryStream();
        using (var pdf = new PdfWriter(ms, leaveOpen: true))
        {
            pdf.AddSvg(svg);
            for (int i = 0; i < 3; i++) pdf.NewPage().DrawSvg(svg, 10, 10, 100, 50, i == 1 ? PdfColor.White : null);
        }
        var r = new PdfTestReader(ms.ToArray());
        var forms = r.Objects.Values.Where(o => o.Dict.Contains("/Subtype /Form")).ToList();
        Assert.Single(forms);
        var ops = Encoding.ASCII.GetString(forms[0].Stream!);
        Assert.Contains(" c ", ops);   // منحنی‌ها
        Assert.Contains("f*", ops);    // evenodd
        Assert.DoesNotContain(" rg", ops); // تک‌رنگ: رنگ هنگام رسم تعیین می‌شود
        foreach (var id in r.PageIds())
        {
            var content = r.Objects[int.Parse(System.Text.RegularExpressions.Regex.Match(r.Objects[id].Dict, @"/Contents (\d+)").Groups[1].Value)].Stream!;
            Assert.Contains("/Sv1 Do", Encoding.ASCII.GetString(content));
        }
    }

    [Fact]
    public void Svg_ArcsAreRejectedClearly()
    {
        var bad = """<svg viewBox="0 0 10 10"><path d="M0 0 A5 5 0 0 1 10 10"/></svg>""";
        Assert.Throws<NotSupportedException>(() => PdfSvg.Parse(bad));
    }
}
