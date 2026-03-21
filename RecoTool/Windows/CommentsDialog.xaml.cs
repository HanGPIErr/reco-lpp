using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecoTool.UI.Models;

namespace RecoTool.Windows
{
    public partial class CommentsDialog : Window
    {
        private List<UserOption> _users = new List<UserOption>();
        private int _mentionStartIndex = -1; // caret position of the '@' that triggered autocomplete

        public CommentsDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Set the list of mentionable users (call before ShowDialog).
        /// </summary>
        public void SetUsers(IEnumerable<UserOption> users)
        {
            _users = users?.Where(u => !string.IsNullOrWhiteSpace(u?.Name)).ToList()
                     ?? new List<UserOption>();
        }

        public void SetConversationText(string comments)
        {
            try
            {
                ConversationTextBox.Text = comments ?? string.Empty;
                ConversationTextBox.CaretIndex = ConversationTextBox.Text.Length;
                ConversationTextBox.ScrollToEnd();
            }
            catch { }
        }

        public string GetNewCommentText()
        {
            try { return NewCommentTextBox.Text; } catch { return null; }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        // --- @Mention Autocomplete ---

        private void NewCommentTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (_users.Count == 0) return;

                var text = NewCommentTextBox.Text ?? "";
                var caret = NewCommentTextBox.CaretIndex;

                // Find the nearest '@' before the caret
                int atPos = -1;
                for (int i = caret - 1; i >= 0; i--)
                {
                    if (text[i] == '@')
                    {
                        atPos = i;
                        break;
                    }
                    // Stop searching if we hit a space/newline before finding @
                    if (text[i] == ' ' || text[i] == '\n' || text[i] == '\r')
                        break;
                }

                if (atPos >= 0)
                {
                    _mentionStartIndex = atPos;
                    var query = text.Substring(atPos + 1, caret - atPos - 1);
                    var filtered = _users
                        .Where(u => u.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(10)
                        .ToList();

                    if (filtered.Count > 0)
                    {
                        MentionListBox.ItemsSource = filtered;
                        MentionListBox.SelectedIndex = 0;
                        MentionAutocompletePopup.IsOpen = true;
                        return;
                    }
                }

                // No match — close popup
                MentionAutocompletePopup.IsOpen = false;
                _mentionStartIndex = -1;
            }
            catch { }
        }

        private void NewCommentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (!MentionAutocompletePopup.IsOpen) return;

                if (e.Key == Key.Down)
                {
                    if (MentionListBox.SelectedIndex < MentionListBox.Items.Count - 1)
                        MentionListBox.SelectedIndex++;
                    e.Handled = true;
                }
                else if (e.Key == Key.Up)
                {
                    if (MentionListBox.SelectedIndex > 0)
                        MentionListBox.SelectedIndex--;
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    InsertSelectedMention();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    MentionAutocompletePopup.IsOpen = false;
                    _mentionStartIndex = -1;
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void MentionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InsertSelectedMention();
        }

        private void MentionListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                InsertSelectedMention();
                e.Handled = true;
            }
        }

        private void InsertSelectedMention()
        {
            try
            {
                var selected = MentionListBox.SelectedItem as UserOption;
                if (selected == null || _mentionStartIndex < 0) return;

                var text = NewCommentTextBox.Text ?? "";
                var caret = NewCommentTextBox.CaretIndex;

                // Replace @query with @username
                string before = text.Substring(0, _mentionStartIndex);
                string after = caret < text.Length ? text.Substring(caret) : "";
                string mention = $"@{selected.Name} ";

                NewCommentTextBox.Text = before + mention + after;
                NewCommentTextBox.CaretIndex = before.Length + mention.Length;

                MentionAutocompletePopup.IsOpen = false;
                _mentionStartIndex = -1;
                NewCommentTextBox.Focus();
            }
            catch { }
        }
    }
}
