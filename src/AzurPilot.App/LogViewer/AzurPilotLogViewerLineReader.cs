using System.Text;

namespace AzurPilot.App.LogViewer;

/// <summary>Построчное чтение канала с жёстким пределом длины строки.</summary>
/// <remarks>
/// <para>
/// Читатель не доверяет второй стороне: строка длиннее предела не буферизуется, а пропускается до
/// ближайшего разделителя и учитывается счётчиком. Поэтому «медленный шутник» на другом конце канала не
/// может неограниченно растить память процесса.
/// </para>
/// <para>
/// Байты строки собираются целиком и декодируются как UTF-8 только после разделителя: многобайтовый
/// символ, попавший на границу чтения, декодируется корректно.
/// </para>
/// </remarks>
internal sealed class AzurPilotLogViewerLineReader
{
    private const int InitialLineCapacity = 512;

    private readonly Stream _stream;
    private readonly int _maximumLineBytes;
    private readonly byte[] _readBuffer = new byte[4096];

    private byte[] _line = new byte[InitialLineCapacity];
    private int _lineLength;
    private int _readStart;
    private int _readEnd;
    private long _rejectedLineCount;

    /// <summary>Создаёт читатель строк для указанного потока канала.</summary>
    /// <param name="stream">Поток канала.</param>
    /// <param name="maximumLineBytes">Предельная длина строки в байтах.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Предел длины строки не положителен.</exception>
    internal AzurPilotLogViewerLineReader(Stream stream, int maximumLineBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineBytes, 1);
        _stream = stream;
        _maximumLineBytes = maximumLineBytes;
    }

    /// <summary>Число строк, отвергнутых из-за превышения предела длины.</summary>
    internal long RejectedLineCount => Interlocked.Read(ref _rejectedLineCount);

    /// <summary>Читает очередную строку канала.</summary>
    /// <param name="cancellationToken">Токен отмены чтения.</param>
    /// <returns>
    /// Строка без разделителя <c>\n</c> либо <see langword="null"/>, если канал закрыт. Строки длиннее
    /// предела пропускаются и в результат не попадают.
    /// </returns>
    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        bool discarding = false;
        _lineLength = 0;

        while (true)
        {
            while (_readStart < _readEnd)
            {
                byte current = _readBuffer[_readStart++];
                if (current == (byte)'\n')
                {
                    if (discarding)
                    {
                        discarding = false;
                        _lineLength = 0;
                        _ = Interlocked.Increment(ref _rejectedLineCount);
                        continue;
                    }

                    return DecodeLine();
                }

                if (discarding)
                {
                    continue;
                }

                if (_lineLength == _maximumLineBytes)
                {
                    discarding = true;
                    continue;
                }

                AppendByte(current);
            }

            int read = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (discarding)
                {
                    _ = Interlocked.Increment(ref _rejectedLineCount);
                    return null;
                }

                return _lineLength == 0 ? null : DecodeLine();
            }

            _readStart = 0;
            _readEnd = read;
        }
    }

    private void AppendByte(byte value)
    {
        if (_lineLength == _line.Length)
        {
            byte[] grown = new byte[Math.Min(_line.Length * 2, _maximumLineBytes)];
            Array.Copy(_line, grown, _lineLength);
            _line = grown;
        }

        _line[_lineLength++] = value;
    }

    private string DecodeLine()
    {
        int length = _lineLength;
        if (length > 0 && _line[length - 1] == (byte)'\r')
        {
            length--;
        }

        _lineLength = 0;
        return Encoding.UTF8.GetString(_line, 0, length);
    }
}
