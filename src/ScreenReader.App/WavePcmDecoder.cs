using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;

namespace ScreenReader.App;

public sealed class WavePcmDecoder
{
    private readonly Stream stream;
    private long remaining;
    public WaveFormat Format { get; }
    public WavePcmDecoder(Stream stream)
    {
        this.stream=stream;
        var riff=new byte[12]; stream.ReadExactly(riff);
        if (Encoding.ASCII.GetString(riff,0,4)!="RIFF" || Encoding.ASCII.GetString(riff,8,4)!="WAVE") throw new InvalidDataException(UiText.T("wave.expected"));
        WaveFormat? format=null;
        long headerBytes=12;
        while (true)
        {
            var header=new byte[8]; stream.ReadExactly(header);
            string id=Encoding.ASCII.GetString(header,0,4);
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (id=="data")
            {
                Format=format ?? throw new InvalidDataException(UiText.T("wave.noFormat"));
                remaining=length is 0 or >= 0x7FFFFFFF ? long.MaxValue : length;
                return;
            }
            headerBytes+=8+length+(length&1);
            if (length>1048576 || headerBytes>1048576) throw new InvalidDataException(UiText.T("wave.largeHeader"));
            var data=new byte[length]; stream.ReadExactly(data);
            if (id=="fmt ")
            {
                if (data.Length<16) throw new InvalidDataException(UiText.T("wave.badFormat"));
                ushort tag=BinaryPrimitives.ReadUInt16LittleEndian(data);
                int channels=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
                int rate=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                int bits=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
                if (channels is <1 or >8 || rate is <8000 or >192000 || bits is not (8 or 16 or 24 or 32)) throw new InvalidDataException(UiText.T("wave.parameters"));
                format=tag switch { 1=>new WaveFormat(rate,bits,channels),3 when bits==32=>WaveFormat.CreateIeeeFloatWaveFormat(rate,channels),_=>throw new InvalidDataException(UiText.T("wave.encoding")) };
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12))!=format.BlockAlign) throw new InvalidDataException(UiText.T("wave.alignment"));
            }
            if ((length&1)!=0 && stream.ReadByte()<0) throw new EndOfStreamException();
        }
    }
    public int Read(byte[] buffer)
    {
        if (remaining==0) return 0;
        int preferred=Math.Max(Format.BlockAlign,Format.AverageBytesPerSecond/10/Format.BlockAlign*Format.BlockAlign);
        int count=stream.Read(buffer,0,(int)Math.Min(Math.Min(buffer.Length,preferred),remaining));
        if (count==0 && remaining!=long.MaxValue) throw new EndOfStreamException(UiText.T("wave.truncated"));
        if (remaining!=long.MaxValue) remaining-=count;
        return count;
    }
}
