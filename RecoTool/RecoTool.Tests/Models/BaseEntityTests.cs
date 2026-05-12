using System;
using System.Threading;
using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Models
{
    /// <summary>
    /// Tests pour <see cref="BaseEntity"/> — squelette de soft-delete + audit version.
    /// </summary>
    public class BaseEntityTests
    {
        private sealed class TestEntity : BaseEntity { }

        [Fact]
        public void Constructor_SetsCreationDateAndVersionOne()
        {
            var before = DateTime.Now.AddSeconds(-1);
            var e = new TestEntity();
            var after = DateTime.Now.AddSeconds(1);

            e.CreationDate.Should().NotBeNull();
            e.CreationDate.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
            e.Version.Should().Be(1);
            e.IsDeleted.Should().BeFalse();
            e.DeleteDate.Should().BeNull();
            e.LastModified.Should().BeNull();
            e.ModifiedBy.Should().BeNull();
        }

        [Fact]
        public void IsDeleted_TrueWhenDeleteDateHasValue()
        {
            var e = new TestEntity { DeleteDate = DateTime.Now };
            e.IsDeleted.Should().BeTrue();
        }

        [Fact]
        public void MarkAsDeleted_SetsDeleteDate()
        {
            var e = new TestEntity();
            e.MarkAsDeleted();
            e.IsDeleted.Should().BeTrue();
            e.DeleteDate.Should().NotBeNull();
        }

        [Fact]
        public void UpdateModification_SetsModifiedByAndIncrementsVersion()
        {
            var e = new TestEntity();
            e.UpdateModification("alice");
            e.ModifiedBy.Should().Be("alice");
            e.LastModified.Should().NotBeNull();
            e.Version.Should().Be(2);
        }

        [Fact]
        public void UpdateModification_MultipleCalls_IncrementVersionEachTime()
        {
            var e = new TestEntity();
            e.UpdateModification("u1");
            e.UpdateModification("u2");
            e.UpdateModification("u3");
            e.Version.Should().Be(4);
            e.ModifiedBy.Should().Be("u3");
        }
    }
}
