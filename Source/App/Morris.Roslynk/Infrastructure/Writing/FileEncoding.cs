using System.Text;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>The encoding to write a file back with, as its byte-order mark declares it: UTF-8 or UTF-16 (either
/// byte order) with their BOM, or UTF-8 without a BOM when the file has none. The text after the BOM starts at
/// <c>Preamble.Length</c>; how strictly to decode it is the caller's choice.</summary>
public static class FileEncoding
{
	private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
	private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
	private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
	private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);

	public static Encoding Detect(ReadOnlySpan<byte> bytes)
	{
		if (bytes.StartsWith(Utf8WithBom.Preamble))
			return Utf8WithBom;
		if (bytes.StartsWith(Utf16LittleEndian.Preamble))
			return Utf16LittleEndian;
		if (bytes.StartsWith(Utf16BigEndian.Preamble))
			return Utf16BigEndian;
		return Utf8WithoutBom;
	}
}
