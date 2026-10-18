namespace UglyToad.PdfPig.Tests.Filters
{
    using PdfPig.Filters;
    using PdfPig.Tokens;

    public class DecodeLimitTests
    {
        private const int Limit = 64 * 1024;

        // Not a power of two, so a buffer the pool rounds up is larger than the limit.
        private const int UnevenLimit = 100_000;

        private static readonly DictionaryToken NoParameters = new DictionaryToken(new Dictionary<NameToken, IToken>());

        [Theory]
        [InlineData(Limit, Limit * 4)]
        [InlineData(UnevenLimit, UnevenLimit + 20_000)]
        public void FlateRefusesStreamOverLimit(int limit, int length)
        {
            var decoder = new FlateFilter(limit);

            Assert.Throws<DecodeLimitExceededException>(() => decoder.Decode(FlateEncode(length), NoParameters, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(Limit, Limit / 2)]
        [InlineData(Limit, Limit)]
        [InlineData(UnevenLimit, UnevenLimit)]
        public void FlateDecodesStreamWithinLimit(int limit, int length)
        {
            var decoded = new FlateFilter(limit).Decode(FlateEncode(length), NoParameters, TestFilterProvider.Instance, 0);

            Assert.Equal(length, decoded.Length);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FlateRefusesPredictedStreamOverLimit(bool statesHeight)
        {
            // With /Height the rows decode straight into the result; without it, into the inflate buffer.
            var (encoded, parameters) = FlatePredicted(Limit * 2 / Columns, statesHeight);

            Assert.Throws<DecodeLimitExceededException>(() => new FlateFilter(Limit).Decode(encoded, parameters, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FlateDecodesPredictedStreamWithinLimit(bool statesHeight)
        {
            var rows = Limit / 2 / Columns;
            var (encoded, parameters) = FlatePredicted(rows, statesHeight);

            Assert.Equal(rows * Columns, new FlateFilter(Limit).Decode(encoded, parameters, TestFilterProvider.Instance, 0).Length);
        }

        [Fact]
        public void FilterChainRefusesLaterStageOverLimit()
        {
            var inner = FlateEncode(Limit * 4);
            var outer = new FlateFilter().Encode(new MemoryStream(inner), NoParameters);
            var stream = new StreamToken(new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, new ArrayToken(new[] { NameToken.FlateDecode, NameToken.FlateDecode }) }
            }), outer);

            Assert.Throws<DecodeLimitExceededException>(() => stream.Decode(new LimitedFilterProvider()));
        }

        [Fact]
        public void RunLengthRefusesStreamOverLimit()
        {
            var decoder = new RunLengthFilter(Limit);

            Assert.Throws<DecodeLimitExceededException>(() => decoder.Decode(RunLengthEncodeZeros(Limit * 4), NoParameters, TestFilterProvider.Instance, 0));
        }

        [Fact]
        public void RunLengthDecodesStreamWithinLimit()
        {
            var decoded = new RunLengthFilter(Limit).Decode(RunLengthEncodeZeros(Limit), NoParameters, TestFilterProvider.Instance, 0);

            Assert.Equal(Limit, decoded.Length);
        }

#if NET
        [Theory]
        [InlineData(Limit, Limit * 4)]
        [InlineData(UnevenLimit, UnevenLimit + 20_000)]
        public void BrotliRefusesStreamOverLimit(int limit, int length)
        {
            var decoder = new BrotliFilter(limit);

            Assert.Throws<DecodeLimitExceededException>(() => decoder.Decode(BrotliEncode(length), NoParameters, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(Limit, Limit)]
        [InlineData(UnevenLimit, UnevenLimit)]
        public void BrotliDecodesStreamWithinLimit(int limit, int length)
        {
            var decoded = new BrotliFilter(limit).Decode(BrotliEncode(length), NoParameters, TestFilterProvider.Instance, 0);

            Assert.Equal(length, decoded.Length);
        }

        [Fact]
        public void RefusedDecodeAllocatesAboutTheLimit()
        {
            // A 16 MiB bomb against a 1 MiB limit; unbounded decoding would allocate the 16 MiB.
            const int limit = 1024 * 1024;
            var flate = FlateEncode(16 * limit);
            var brotli = BrotliEncode(16 * limit);

            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<DecodeLimitExceededException>(() => new FlateFilter(limit).Decode(flate, NoParameters, TestFilterProvider.Instance, 0));
            Assert.Throws<DecodeLimitExceededException>(() => new BrotliFilter(limit).Decode(brotli, NoParameters, TestFilterProvider.Instance, 0));

            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 * limit);
        }

        private static byte[] BrotliEncode(int length)
        {
            using var output = new MemoryStream();
            using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest))
            {
                brotli.Write(new byte[length]);
            }

            return output.ToArray();
        }
#endif

        private const int Columns = 100;

        /// <summary>
        /// Rows of zeros, each behind a PNG None filter byte, with the predictor in /DecodeParms.
        /// </summary>
        private static (byte[] Encoded, DictionaryToken Parameters) FlatePredicted(int rows, bool statesHeight)
        {
            var data = new byte[rows * (Columns + 1)];
            var entries = new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.FlateDecode },
                {
                    NameToken.DecodeParms, new DictionaryToken(new Dictionary<NameToken, IToken>
                    {
                        { NameToken.Predictor, new NumericToken(12) },
                        { NameToken.Columns, new NumericToken(Columns) }
                    })
                }
            };

            if (statesHeight)
            {
                // Far fewer rows than the stream holds, so the result has to grow.
                entries[NameToken.Height] = new NumericToken(10);
            }

            return (new FlateFilter().Encode(new MemoryStream(data), NoParameters), new DictionaryToken(entries));
        }

        private static byte[] FlateEncode(int length)
        {
            return new FlateFilter().Encode(new MemoryStream(new byte[length]), NoParameters);
        }

        /// <summary>
        /// Runs of 128 zero bytes, each two bytes long.
        /// </summary>
        private static byte[] RunLengthEncodeZeros(int length)
        {
            var encoded = new List<byte>();
            for (var i = 0; i < length / 128; i++)
            {
                encoded.Add(129);
                encoded.Add(0);
            }

            encoded.Add(128);
            return encoded.ToArray();
        }

        private sealed class LimitedFilterProvider : BaseFilterProvider
        {
            public LimitedFilterProvider()
                : base(new Dictionary<string, IFilter> { { NameToken.FlateDecode.Data, new FlateFilter(Limit) } })
            {
            }
        }
    }
}
