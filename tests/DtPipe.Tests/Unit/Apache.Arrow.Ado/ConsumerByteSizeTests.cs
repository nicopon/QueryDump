using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Ado;
using Apache.Arrow.Ado.Consumer;
using Apache.Arrow.Types;
using Moq;
using Xunit;

namespace DtPipe.Tests.Unit.ApacheArrowAdo;

public class ConsumerByteSizeTests
{
    [Fact]
    public void FixedWidthConsumer_CountsTypeWidthPerRow()
    {
        var reader = new Mock<DbDataReader>();
        reader.Setup(r => r.IsDBNull(0)).Returns(false);
        reader.Setup(r => r.GetInt32(0)).Returns(7);

        var consumer = new Int32Consumer(0);
        consumer.Consume(reader.Object);
        consumer.Consume(reader.Object);

        Assert.Equal(8, consumer.EstimatedByteSize); // 2 rows * 4 bytes
    }

    [Fact]
    public void StringConsumer_CountsUtf8PayloadPlusOffsetSlot()
    {
        var reader = new Mock<DbDataReader>();
        reader.Setup(r => r.IsDBNull(0)).Returns(false);
        reader.SetupSequence(r => r.GetString(0))
              .Returns("abc")     // 3 bytes
              .Returns("é");  // 'é' → 2 bytes UTF-8

        var consumer = new StringConsumer(0);
        consumer.Consume(reader.Object);
        consumer.Consume(reader.Object);

        // 2 offset slots (4 each) + 3 + 2 payload
        Assert.Equal(13, consumer.EstimatedByteSize);
    }

    [Fact]
    public void Reset_ZeroesTheEstimate()
    {
        var reader = new Mock<DbDataReader>();
        reader.Setup(r => r.IsDBNull(0)).Returns(false);
        reader.Setup(r => r.GetString(0)).Returns("hello");

        var consumer = new StringConsumer(0);
        consumer.Consume(reader.Object);
        Assert.True(consumer.EstimatedByteSize > 0);

        consumer.Reset();
        Assert.Equal(0, consumer.EstimatedByteSize);
    }

    [Theory]
    [InlineData(typeof(BooleanType), 1)]
    [InlineData(typeof(Int32Type), 4)]
    [InlineData(typeof(Int64Type), 8)]
    [InlineData(typeof(DoubleType), 8)]
    [InlineData(typeof(StringType), 4)]
    [InlineData(typeof(BinaryType), 4)]
    public void ArrowByteSize_FixedWidth_MatchesStorageWidth(System.Type arrowTypeClr, int expected)
    {
        var arrowType = arrowTypeClr == typeof(BooleanType) ? (IArrowType)BooleanType.Default
            : arrowTypeClr == typeof(Int32Type) ? Int32Type.Default
            : arrowTypeClr == typeof(Int64Type) ? Int64Type.Default
            : arrowTypeClr == typeof(DoubleType) ? DoubleType.Default
            : arrowTypeClr == typeof(StringType) ? StringType.Default
            : BinaryType.Default;

        Assert.Equal(expected, ArrowByteSize.FixedWidth(arrowType));
    }
}
