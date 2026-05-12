using System.Runtime.CompilerServices;

// Allow the unit-test assembly to access internal members for white-box testing
// (ex: RulesEngine.__TestSeedRules, internal helpers).
[assembly: InternalsVisibleTo("RecoTool.Tests")]
