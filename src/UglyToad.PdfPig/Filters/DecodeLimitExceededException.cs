namespace UglyToad.PdfPig.Filters
{
    using Core;

    /// <summary>
    /// A stream would decode to more than a filter decodes one stream to.
    /// </summary>
    internal sealed class DecodeLimitExceededException : PdfDocumentFormatException
    {
        public DecodeLimitExceededException(int limit)
            : base($"The stream decodes to more than {limit} bytes, the most a filter decodes one stream to.")
        {
        }
    }
}
