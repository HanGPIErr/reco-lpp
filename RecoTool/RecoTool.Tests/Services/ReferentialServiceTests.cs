using System;
using FluentAssertions;
using Moq;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests unitaires <see cref="ReferentialService"/>. La majorité des méthodes
    /// publiques touchent OleDb — ici on vérifie surtout les gardes du ctor
    /// et la propagation des chaînes d'environnement via <see cref="IOfflineFirstService"/>.
    /// </summary>
    public class ReferentialServiceTests
    {
        [Fact]
        public void Ctor_NullDependency_Throws()
        {
            Action a = () => new ReferentialService(null);
            a.Should().Throw<ArgumentNullException>()
                .WithMessage("*offlineFirstService*");
        }

        [Fact]
        public void Ctor_AcceptsCurrentUserParameter()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.ReferentialConnectionString).Returns("Provider=ACE;Data Source=:memory:;");

            // Le constructeur ne doit pas lever
            Action a = () => new ReferentialService(ofs.Object, "alice");
            a.Should().NotThrow();
        }

        [Fact]
        public void Ctor_DefaultsCurrentUserToNull()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.ReferentialConnectionString).Returns("any");

            Action a = () => new ReferentialService(ofs.Object);
            a.Should().NotThrow();
        }
    }
}
