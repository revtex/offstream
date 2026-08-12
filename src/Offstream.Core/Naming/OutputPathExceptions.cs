namespace Offstream.Core.Naming;

/// <summary>The file being renamed no longer exists.</summary>
public sealed class SourceFileNotFoundException : IOException
{
    public SourceFileNotFoundException()
    {
    }

    public SourceFileNotFoundException(string message) : base(message)
    {
    }

    public SourceFileNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The directory a file was to be renamed into no longer exists.</summary>
public sealed class DestinationPathNotFoundException : IOException
{
    public DestinationPathNotFoundException()
    {
    }

    public DestinationPathNotFoundException(string message) : base(message)
    {
    }

    public DestinationPathNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A track that cannot be turned into a file name, e.g. one with no artist.</summary>
public sealed class UnrecognizedTrackException : InvalidOperationException
{
    public UnrecognizedTrackException()
    {
    }

    public UnrecognizedTrackException(string message) : base(message)
    {
    }

    public UnrecognizedTrackException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
