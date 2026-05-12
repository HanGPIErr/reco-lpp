using System;
using FluentAssertions;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="ThemeManager"/>. Sans Application.Current actif,
    /// ApplyTheme() retourne tôt — on valide donc le toggle du flag, l'événement
    /// ThemeChanged et la robustesse de Initialize/SavePreference.
    /// </summary>
    public class ThemeManagerTests
    {
        // Les tests partagent un état statique → on les exécute en série en remettant le flag.
        public ThemeManagerTests()
        {
            // Reset à light mode avant chaque test, sans tenir compte de l'événement précédent
            ResetSilently(false);
        }

        private static void ResetSilently(bool dark)
        {
            // Si déjà dans cet état, le setter ne déclenche rien.
            if (ThemeManager.IsDarkMode != dark)
                ThemeManager.IsDarkMode = dark;
        }

        [Fact]
        public void Default_IsLightMode()
        {
            ThemeManager.IsDarkMode.Should().BeFalse();
        }

        [Fact]
        public void ToggleTheme_FlipsFlagAndRaisesEvent()
        {
            int events = 0;
            EventHandler handler = (_, __) => events++;
            ThemeManager.ThemeChanged += handler;
            try
            {
                ThemeManager.ToggleTheme();
                ThemeManager.IsDarkMode.Should().BeTrue();
                events.Should().Be(1);

                ThemeManager.ToggleTheme();
                ThemeManager.IsDarkMode.Should().BeFalse();
                events.Should().Be(2);
            }
            finally
            {
                ThemeManager.ThemeChanged -= handler;
            }
        }

        [Fact]
        public void IsDarkMode_SetSameValue_DoesNotRaiseEvent()
        {
            int events = 0;
            EventHandler handler = (_, __) => events++;
            ThemeManager.ThemeChanged += handler;
            try
            {
                ThemeManager.IsDarkMode = false; // déjà false
                events.Should().Be(0);
            }
            finally
            {
                ThemeManager.ThemeChanged -= handler;
            }
        }

        [Fact]
        public void Initialize_SetsLightMode()
        {
            ThemeManager.IsDarkMode = true;
            ThemeManager.Initialize();
            ThemeManager.IsDarkMode.Should().BeFalse();
        }

        [Fact]
        public void SavePreference_DoesNotThrow()
        {
            Action act = () => ThemeManager.SavePreference();
            act.Should().NotThrow();
        }
    }
}
