using System.Collections.Generic;
using ValheimMetrics.Chests;
using Xunit;
using static ValheimMetrics.Tests.F;

namespace ValheimMetrics.Tests
{
    public class MarkCodecTests
    {
        [Fact]
        public void RoundTrips()
        {
            var marks = new List<SlotMark> { Mark(0, 0, Wood), Mark(7, 3, new ItemKind("TrophyDeer", 2, 1)) };

            string text = MarkCodec.Encode(marks);

            Assert.Equal("1|0,0,Wood,1,0|7,3,TrophyDeer,2,1", text);
            Assert.Equal(marks, MarkCodec.Decode(text));
        }

        [Fact]
        public void NoMarksIsEmptyText()
        {
            Assert.Equal("", MarkCodec.Encode(new List<SlotMark>()));
            Assert.Empty(MarkCodec.Decode(""));
            Assert.Empty(MarkCodec.Decode(null));
        }

        [Theory]
        [InlineData("2|0,0,Wood,1,0")]
        [InlineData("garbage")]
        [InlineData("1|0,0,,1,0")]
        [InlineData("1|a,0,Wood,1,0")]
        [InlineData("1|-1,0,Wood,1,0")]
        [InlineData("1|0,0,Wood,1")]
        public void UnreadableEntriesAreSkipped(string text)
        {
            Assert.Empty(MarkCodec.Decode(text));
        }

        [Fact]
        public void KeepsReadableEntriesNextToBrokenOnes()
        {
            var marks = MarkCodec.Decode("1|0,0,Wood,1,0|junk|1,0,Stone,1,0");

            Assert.Equal(new[] { Mark(0, 0, Wood), Mark(1, 0, Stone) }, marks);
        }

        [Fact]
        public void PrefabsThatWouldBreakTheFormatAreNotWritten()
        {
            var marks = new List<SlotMark> { Mark(0, 0, new ItemKind("a|b", 1, 0)), Mark(1, 0, new ItemKind("a,b", 1, 0)), Mark(2, 0, Wood) };

            Assert.Equal("1|2,0,Wood,1,0", MarkCodec.Encode(marks));
        }
    }
}
