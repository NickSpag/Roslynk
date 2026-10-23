using System.Text;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Infrastructure.Writing;

public class FileEncodingTests
{
	[Theory]
	[InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, 65001, 3)]
	[InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, 1200, 2)]
	[InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, 1201, 2)]
	public void WhenAFileStartsWithABom_ThenItsEncodingKeepsThatBom(byte[] bytes, int codePage, int preambleLength)
	{
		Encoding encoding = FileEncoding.Detect(bytes);

		Assert.Equal(codePage, encoding.CodePage);
		Assert.Equal(bytes.Take(preambleLength), encoding.Preamble.ToArray());
	}

	[Theory]
	[InlineData(new byte[0])]
	[InlineData(new byte[] { 0x41, 0x42 })]
	[InlineData(new byte[] { 0xEF, 0xBB })]
	public void WhenAFileHasNoBom_ThenItIsUtf8WithoutABom(byte[] bytes)
	{
		Encoding encoding = FileEncoding.Detect(bytes);

		Assert.Equal(65001, encoding.CodePage);
		Assert.Empty(encoding.Preamble.ToArray());
	}
}
