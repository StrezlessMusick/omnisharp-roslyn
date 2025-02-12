﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using OmniSharp.Models.Diagnostics;
using TestUtility;
using Xunit;
using Xunit.Abstractions;

namespace OmniSharp.Roslyn.CSharp.Tests
{
    public class CustomRoslynAnalyzerFacts
    {
        public class TestAnalyzerReference : AnalyzerReference
        {
            private readonly string _id;
            private readonly bool _isEnabledByDefault;

            public TestAnalyzerReference(string testAnalyzerId, bool isEnabledByDefault = true)
            {
                _id = testAnalyzerId;
                _isEnabledByDefault = isEnabledByDefault;
            }

            public override string FullPath => null;
            public override object Id => _id;
            public override string Display => $"{nameof(TestAnalyzerReference)}_{Id}";

            public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language)
            {
                return new DiagnosticAnalyzer[] { new TestDiagnosticAnalyzer(Id.ToString(), _isEnabledByDefault) }.ToImmutableArray();
            }

            public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages()
            {
                return new DiagnosticAnalyzer[] { new TestDiagnosticAnalyzer(Id.ToString(), _isEnabledByDefault) }.ToImmutableArray();
            }
        }

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        public class TestDiagnosticAnalyzer : DiagnosticAnalyzer
        {
            public TestDiagnosticAnalyzer(string id, bool isEnabledByDefault)
            {
                this.id = id;
                _isEnabledByDefault = isEnabledByDefault;
            }

            private DiagnosticDescriptor Rule => new DiagnosticDescriptor(
                this.id,
                "Testtitle",
                "Type name '{0}' contains lowercase letters",
                "Naming",
                DiagnosticSeverity.Error,
                isEnabledByDefault: _isEnabledByDefault
            );

            private readonly string id;
            private readonly bool _isEnabledByDefault;

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            {
                get { return ImmutableArray.Create(Rule); }
            }

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
                context.EnableConcurrentExecution();
                context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
            }

            private void AnalyzeSymbol(SymbolAnalysisContext context)
            {
                var namedTypeSymbol = (INamedTypeSymbol)context.Symbol;
                if (namedTypeSymbol.Name == "_this_is_invalid_test_class_name")
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        namedTypeSymbol.Locations[0],
                        namedTypeSymbol.Name
                    ));
                }
            }
        }

        private readonly ITestOutputHelper _testOutput;

        public CustomRoslynAnalyzerFacts(ITestOutputHelper testOutput)
        {
            _testOutput = testOutput;
        }

        [Fact]
        public async Task When_custom_analyzers_are_executed_then_return_results()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_66.cs", "class _this_is_invalid_test_class_name { int n = true; }");

                var testAnalyzerRef = new TestAnalyzerReference("TS1100");

                var projectIds = AddProjectWithFile(host, testFile, testAnalyzerRef);

                var result = await host.RequestCodeCheckAsync("testFile_66.cs");

                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());
            }
        }

        private OmniSharpTestHost GetHost(bool analyzeOpenDocumentsOnly = false)
        {
            return OmniSharpTestHost.Create(testOutput: _testOutput,
                configurationData: TestHelpers.GetConfigurationDataWithAnalyzerConfig(roslynAnalyzersEnabled: true, analyzeOpenDocumentsOnly: analyzeOpenDocumentsOnly));
        }

        [Fact]
        public async Task Always_return_results_from_net_default_analyzers()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_1.cs", "class SomeClass { int n = true; }");

                AddProjectWithFile(host, testFile);

                var result = await host.RequestCodeCheckAsync(testFile.FileName);

                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>().Where(x => x.FileName == testFile.FileName), f => f.Id.Contains("CS"));
            }
        }

        [Fact]
        public async Task Rulesets_should_work_with_syntax_analyzers()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_9.cs", @"
                    class Program
                    {
                        static void Main(string[] args)
                        {
                            return;
                            Console.WriteLine(null); // This is CS0162, unreachable code.
                        }
                    }");

                var projectId = AddProjectWithFile(host, testFile);
                var testRules = CreateRules("CS0162", ReportDiagnostic.Hidden);

                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRules.ToImmutableDictionary());

                var result = await host.RequestCodeCheckAsync(testFile.FileName);

                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == "CS0162" && f.LogLevel == "Hidden");
            }
        }

        [Fact]
        public async Task When_rules_udpate_diagnostic_severity_then_show_them_with_new_severity()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_2.cs", "class _this_is_invalid_test_class_name { int n = true; }");

                var testAnalyzerRef = new TestAnalyzerReference("TS1100");

                var projectId = AddProjectWithFile(host, testFile, testAnalyzerRef);
                var testRules = CreateRules(testAnalyzerRef.Id.ToString(), ReportDiagnostic.Hidden);

                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRules.ToImmutableDictionary());

                var result = await host.RequestCodeCheckAsync("testFile_2.cs");

                var bar = result.QuickFixes.ToList();
                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString() && f.LogLevel == "Hidden");
            }
        }

        private static Dictionary<string, ReportDiagnostic> CreateRules(string analyzerId, ReportDiagnostic diagnostic)
        {
            return new Dictionary<string, ReportDiagnostic>
            {
                { analyzerId, diagnostic }
            };
        }

        [Fact]
        // This is important because hidden still allows code fixes to execute, not prevents it, for this reason suppressed analytics should not be returned at all.
        public async Task When_custom_rule_is_set_to_none_dont_return_results_at_all()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_3.cs", "class _this_is_invalid_test_class_name { int n = true; }");

                var testAnalyzerRef = new TestAnalyzerReference("TS1101");

                var projectId = AddProjectWithFile(host, testFile, testAnalyzerRef);

                var testRules = CreateRules(testAnalyzerRef.Id.ToString(), ReportDiagnostic.Suppress);

                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRules.ToImmutableDictionary());

                var result = await host.RequestCodeCheckAsync("testFile_3.cs");
                Assert.DoesNotContain(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());
            }
        }

        [Fact(Skip = "https://github.com/dotnet/roslyn/issues/66085")]
        public async Task When_diagnostic_is_disabled_by_default_updating_rule_will_enable_it()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_4.cs", "class _this_is_invalid_test_class_name { int n = true; }");

                var testAnalyzerRef = new TestAnalyzerReference("TS1101", isEnabledByDefault: false);

                var projectId = AddProjectWithFile(host, testFile, testAnalyzerRef);

                var testRules = CreateRules(testAnalyzerRef.Id.ToString(), ReportDiagnostic.Error);

                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRules.ToImmutableDictionary());

                var result = await host.RequestCodeCheckAsync("testFile_4.cs");
                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());
            }
        }

        [Fact]
        public async Task WhenDiagnosticsRulesAreUpdated_ThenReAnalyzerFilesInProject()
        {
            using (var host = GetHost())
            {
                var testFile = new TestFile("testFile_4.cs", "class _this_is_invalid_test_class_name { int n = true; }");

                var testAnalyzerRef = new TestAnalyzerReference("TS1101", isEnabledByDefault: false);

                var projectId = AddProjectWithFile(host, testFile, testAnalyzerRef);
                var testRulesOriginal = CreateRules(testAnalyzerRef.Id.ToString(), ReportDiagnostic.Error);
                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRulesOriginal.ToImmutableDictionary());
                await host.RequestCodeCheckAsync("testFile_4.cs");

                var testRulesUpdated = CreateRules(testAnalyzerRef.Id.ToString(), ReportDiagnostic.Suppress);

                var workspaceUpdatedCheck = new AutoResetEvent(false);
                host.Workspace.WorkspaceChanged += (_, e) => { if (e.Kind == WorkspaceChangeKind.ProjectChanged) { workspaceUpdatedCheck.Set(); } };
                host.Workspace.UpdateDiagnosticOptionsForProject(projectId, testRulesUpdated.ToImmutableDictionary());

                Assert.True(workspaceUpdatedCheck.WaitOne(timeout: TimeSpan.FromSeconds(15)));

                var result = await host.RequestCodeCheckAsync("testFile_4.cs");
                Assert.DoesNotContain(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());
            }
        }


        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task WhenDocumentIsntOpenAndAnalyzeOpenDocumentsOnlyIsSet_DontAnalyzeFiles(bool analyzeOpenDocumentsOnly, bool isDocumentOpen)
        {
            using (var host = GetHost(analyzeOpenDocumentsOnly))
            {
                var testFile = new TestFile("testFile.cs", "class _this_is_invalid_test_class_name { int n = true; }");
                var testAnalyzerRef = new TestAnalyzerReference("TS1100");

                AddProjectWithFile(host, testFile, testAnalyzerRef);

                if (isDocumentOpen)
                {
                    var doc = host.Workspace.GetDocument("testFile.cs");

                    host.Workspace.OpenDocument(doc.Id);
                }

                var expectedDiagnosticCount = analyzeOpenDocumentsOnly && !isDocumentOpen ? 1 : 2;

                var result = await host.RequestCodeCheckAsync("testFile.cs");

                if (analyzeOpenDocumentsOnly && !isDocumentOpen)
                    Assert.DoesNotContain(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());
                else
                    Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == testAnalyzerRef.Id.ToString());

                Assert.Contains(result.QuickFixes.OfType<DiagnosticLocation>(), f => f.Id == "CS0029");
            }
        }

        private ProjectId AddProjectWithFile(OmniSharpTestHost host, TestFile testFile, TestAnalyzerReference testAnalyzerRef = null)
        {
            var analyzerReferences = testAnalyzerRef == null ? default :
                new AnalyzerReference[] { testAnalyzerRef }.ToImmutableArray();

            return TestHelpers.AddProjectToWorkspace(
                            host.Workspace,
                            "project.csproj",
                            new[] { "netcoreapp3.1" },
                            new[] { testFile },
                            analyzerRefs: analyzerReferences)
                    .Single();
        }
    }
}
