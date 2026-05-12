using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    public class ViewModelBaseTests
    {
        private sealed class Sample : ViewModelBase
        {
            private int _count;
            public int Count
            {
                get => _count;
                set => SetField(ref _count, value);
            }

            private string _name;
            public string Name
            {
                get => _name;
                set => SetField(ref _name, value);
            }
        }

        [Fact]
        public void SetField_ChangesValue_RaisesPropertyChangedOnce()
        {
            var sut = new Sample();
            var notified = new List<string>();
            sut.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            sut.Count = 1;
            notified.Should().ContainSingle().Which.Should().Be(nameof(Sample.Count));
            sut.Count.Should().Be(1);
        }

        [Fact]
        public void SetField_SameValue_DoesNotRaise()
        {
            var sut = new Sample { Count = 1 };
            int hits = 0;
            sut.PropertyChanged += (_, __) => hits++;

            sut.Count = 1; // identique → ignoré
            hits.Should().Be(0);
        }

        [Fact]
        public void SetField_NullToValue_RaisesNotification()
        {
            var sut = new Sample();
            int hits = 0;
            sut.PropertyChanged += (_, __) => hits++;

            sut.Name = "alice";
            hits.Should().Be(1);
            sut.Name = null;
            hits.Should().Be(2);
        }
    }
}
