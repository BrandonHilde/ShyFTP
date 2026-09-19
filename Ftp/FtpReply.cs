namespace ShyFtp.Ftp;

public sealed class FtpException : Exception
{
    public int Code { get; }

    public FtpException(int code, string message) : base(message)
    {
        Code = code;
    }

    public FtpException(string message) : base(message)
    {
        Code = -1;
    }
}

public sealed class FtpReply
{
    public int Code { get; }
    public IReadOnlyList<string> Lines { get; }

    public FtpReply(int code, IReadOnlyList<string> lines)
    {
        Code = code;
        Lines = lines;
    }

    public string Message => string.Join(" ", Lines);

    public override string ToString() => $"{Code} {string.Join(" | ", Lines)}";
}

public sealed class FtpListItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsSymlink { get; set; }
    public long Size { get; set; }
    public DateTime? ModifiedUtc { get; set; }
}