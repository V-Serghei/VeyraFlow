using System.IO.Compression;
using System.Text;
using Veyra.Application.Services.Diff;

namespace Veyra.Application.Tests;

public sealed class WordSemanticProjectionTests
{
    [Fact]
    public void ExtractSemanticLines_MergesAdjacentEquivalentRunsIntoSingleSemanticLine()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "veyra-word-tests", $"{Guid.NewGuid():N}.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            CreateWordDocument(
                tempPath,
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p>
                      <w:r>
                        <w:rPr><w:b /></w:rPr>
                        <w:t>byte</w:t>
                      </w:r>
                      <w:r>
                        <w:t xml:space="preserve"> </w:t>
                      </w:r>
                      <w:r>
                        <w:rPr><w:b /></w:rPr>
                        <w:t>rowPins</w:t>
                      </w:r>
                      <w:r>
                        <w:rPr><w:b /></w:rPr>
                        <w:t xml:space="preserve">[] = {</w:t>
                      </w:r>
                    </w:p>
                  </w:body>
                </w:document>
                """);

            var lines = WordSemanticProjection.ExtractSemanticLines(tempPath);

            Assert.Collection(
                lines,
                line => Assert.Equal("P0001 [p:default]", line),
                line => Assert.Equal("P0001.R0001 [r:b] byte rowPins[] = {", line));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ExtractSemanticLines_KeepsDistinctVisualStylesAsSeparateSemanticLines()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "veyra-word-tests", $"{Guid.NewGuid():N}.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            CreateWordDocument(
                tempPath,
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p>
                      <w:r>
                        <w:rPr><w:b /></w:rPr>
                        <w:t>before</w:t>
                      </w:r>
                      <w:r>
                        <w:rPr><w:i /></w:rPr>
                        <w:t>after</w:t>
                      </w:r>
                    </w:p>
                  </w:body>
                </w:document>
                """);

            var lines = WordSemanticProjection.ExtractSemanticLines(tempPath);

            Assert.Collection(
                lines,
                line => Assert.Equal("P0001 [p:default]", line),
                line => Assert.Equal("P0001.R0001 [r:b] before", line),
                line => Assert.Equal("P0001.R0002 [r:i] after", line));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void CreateWordDocument(string path, string documentXml)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var contentTypes = archive.CreateEntry("[Content_Types].xml");
        using (var stream = contentTypes.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml" ContentType="application/xml" />
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
                </Types>
                """);
        }

        var documentEntry = archive.CreateEntry("word/document.xml");
        using (var stream = documentEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(documentXml);
        }
    }
}
