using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace RecoTool.Windows
{
    public partial class PreviewTextDialog : Window
    {
        private string _rawContent;

        // VS Code dark-theme inspired colors
        private static readonly SolidColorBrush TagBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));       // blue — tag names & angle brackets
        private static readonly SolidColorBrush AttrNameBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));   // light blue — attribute names
        private static readonly SolidColorBrush AttrValueBrush = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));  // orange — attribute values
        private static readonly SolidColorBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));       // light gray — text content
        private static readonly SolidColorBrush CommentBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));    // green — comments
        private static readonly SolidColorBrush PrologBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));     // gray — <?xml ... ?>

        public PreviewTextDialog()
        {
            InitializeComponent();
        }

        public void SetTitle(string title)
        {
            try { TitleBlock.Text = title ?? string.Empty; } catch { }
        }

        public void SetContent(string text)
        {
            _rawContent = text ?? string.Empty;

            if (TryFormatAsXml(_rawContent, out var formattedXml))
            {
                ShowXmlView(formattedXml);
            }
            else
            {
                ShowPlainTextView(_rawContent);
            }
        }

        private bool TryFormatAsXml(string text, out string formatted)
        {
            formatted = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith("<"))
                return false;

            try
            {
                var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                formatted = doc.Declaration != null
                    ? doc.Declaration.ToString() + Environment.NewLine + doc.Root?.ToString()
                    : doc.ToString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ShowXmlView(string formattedXml)
        {
            try
            {
                FormatBadge.Text = "XML";
                XmlRichTextBox.Visibility = Visibility.Visible;
                ContentTextBox.Visibility = Visibility.Collapsed;

                var doc = new FlowDocument
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    Foreground = TextBrush,
                    PageWidth = 2000  // prevent wrapping in XML view
                };

                var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1.4 };
                HighlightXml(paragraph, formattedXml);
                doc.Blocks.Add(paragraph);

                XmlRichTextBox.Document = doc;

                var lines = formattedXml.Split('\n').Length;
                StatusText.Text = $"{lines} lines | {formattedXml.Length:N0} chars";
            }
            catch
            {
                // Fallback to plain text if highlighting fails
                ShowPlainTextView(_rawContent);
            }
        }

        private void ShowPlainTextView(string text)
        {
            FormatBadge.Text = "Text";
            XmlRichTextBox.Visibility = Visibility.Collapsed;
            ContentTextBox.Visibility = Visibility.Visible;
            ContentTextBox.Text = text;
            ContentTextBox.CaretIndex = 0;
            ContentTextBox.ScrollToHome();

            var lines = text.Split('\n').Length;
            StatusText.Text = $"{lines} lines | {text.Length:N0} chars";
        }

        private void HighlightXml(Paragraph paragraph, string xml)
        {
            // Regex-based tokenizer for XML syntax highlighting
            // Matches: comments, processing instructions, CDATA, tags (open/close/self-closing), and text
            var pattern = @"(<!--[\s\S]*?-->)|(<\?[\s\S]*?\?>)|(<!\[CDATA\[[\s\S]*?\]\]>)|(<\/?\w[\w:.-]*(?:\s+[\w:.-]+\s*=\s*""[^""]*""|\s+[\w:.-]+\s*=\s*'[^']*')*\s*\/?>)|([^<]+)";

            var matches = Regex.Matches(xml, pattern);
            foreach (Match m in matches)
            {
                if (m.Groups[1].Success)
                {
                    // Comment
                    paragraph.Inlines.Add(new Run(m.Value) { Foreground = CommentBrush });
                }
                else if (m.Groups[2].Success)
                {
                    // Processing instruction (<?xml ... ?>)
                    paragraph.Inlines.Add(new Run(m.Value) { Foreground = PrologBrush });
                }
                else if (m.Groups[3].Success)
                {
                    // CDATA
                    paragraph.Inlines.Add(new Run(m.Value) { Foreground = TextBrush });
                }
                else if (m.Groups[4].Success)
                {
                    // Tag — parse into sub-parts for fine-grained highlighting
                    HighlightTag(paragraph, m.Value);
                }
                else if (m.Groups[5].Success)
                {
                    // Text content
                    paragraph.Inlines.Add(new Run(m.Value) { Foreground = TextBrush });
                }
            }
        }

        private void HighlightTag(Paragraph paragraph, string tag)
        {
            // Split tag into: angle bracket + name, attributes, closing
            // Examples: <Saa:Revision>, </Saa:Header>, <Tag attr="val" />
            var tagPattern = @"^(<\/?)(\w[\w:.-]*)(\s+(?:[\w:.-]+\s*=\s*(?:""[^""]*""|'[^']*'))\s*)*(\s*\/?>)$";
            var tm = Regex.Match(tag, tagPattern, RegexOptions.Singleline);
            if (!tm.Success)
            {
                // Fallback: render whole tag in tag color
                paragraph.Inlines.Add(new Run(tag) { Foreground = TagBrush });
                return;
            }

            // Opening bracket: < or </
            paragraph.Inlines.Add(new Run(tm.Groups[1].Value) { Foreground = TagBrush });
            // Tag name
            paragraph.Inlines.Add(new Run(tm.Groups[2].Value) { Foreground = TagBrush });

            // Attributes (if any)
            if (tm.Groups[3].Success && !string.IsNullOrEmpty(tm.Groups[3].Value))
            {
                var attrStr = tm.Groups[3].Value;
                var attrPattern = @"([\w:.-]+)(\s*=\s*)(""[^""]*""|'[^']*')";
                int lastIdx = 0;
                foreach (Match am in Regex.Matches(attrStr, attrPattern))
                {
                    // Whitespace before attribute
                    if (am.Index > lastIdx)
                        paragraph.Inlines.Add(new Run(attrStr.Substring(lastIdx, am.Index - lastIdx)) { Foreground = TextBrush });

                    // Attribute name
                    paragraph.Inlines.Add(new Run(am.Groups[1].Value) { Foreground = AttrNameBrush });
                    // Equals sign
                    paragraph.Inlines.Add(new Run(am.Groups[2].Value) { Foreground = TextBrush });
                    // Attribute value (with quotes)
                    paragraph.Inlines.Add(new Run(am.Groups[3].Value) { Foreground = AttrValueBrush });

                    lastIdx = am.Index + am.Length;
                }
                // Trailing whitespace
                if (lastIdx < attrStr.Length)
                    paragraph.Inlines.Add(new Run(attrStr.Substring(lastIdx)) { Foreground = TextBrush });
            }

            // Closing bracket: > or />
            paragraph.Inlines.Add(new Run(tm.Groups[4].Value) { Foreground = TagBrush });
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_rawContent ?? string.Empty);
                StatusText.Text = "Copied to clipboard!";
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try { this.Close(); } catch { }
        }
    }
}
