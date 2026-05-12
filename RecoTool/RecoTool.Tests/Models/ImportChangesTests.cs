using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Models
{
    /// <summary>
    /// Tests pour <see cref="ImportChanges"/> — agrégation Add/Update/Archive.
    /// </summary>
    public class ImportChangesTests
    {
        [Fact]
        public void New_HasEmptyListsAndZeroTotal()
        {
            var c = new ImportChanges();
            c.ToAdd.Should().BeEmpty();
            c.ToUpdate.Should().BeEmpty();
            c.ToArchive.Should().BeEmpty();
            c.TotalChanges.Should().Be(0);
        }

        [Fact]
        public void TotalChanges_IsSumOfThreeBuckets()
        {
            var c = new ImportChanges();
            c.ToAdd.Add(new DataAmbre());
            c.ToAdd.Add(new DataAmbre());
            c.ToUpdate.Add(new DataAmbre());
            c.ToArchive.Add(new DataAmbre());
            c.ToArchive.Add(new DataAmbre());
            c.ToArchive.Add(new DataAmbre());

            c.TotalChanges.Should().Be(6);
        }
    }
}
