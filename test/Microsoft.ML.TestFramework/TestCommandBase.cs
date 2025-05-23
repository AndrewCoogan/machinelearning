﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.Command;
using Microsoft.ML.Data;
using Microsoft.ML.Internal.Utilities;
using Microsoft.ML.Model;
using Microsoft.ML.Runtime;
using Microsoft.ML.TestFramework;
using Microsoft.ML.TestFramework.Attributes;
using Microsoft.ML.TestFrameworkCommon;
using Microsoft.ML.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.ML.RunTests
{
    public abstract partial class TestCommandBase : TestDataViewBase
    {
        protected const string Cat = "Command";

        /// <summary>
        /// Wrapper for a path to some file that will be output by some process within the
        /// tests of commands. This class comes up with a suitable path derived from a user
        /// provided suffix and the test name, and then allows that path to be easily phrased
        /// as an argument to input commands and tested for equality versus its baseline.
        /// </summary>
        protected sealed class OutputPath
        {
            private readonly string _dir;
            private readonly string _name;
            public readonly string Path;

            private readonly TestCommandBase _testCmd;

            private bool CanBeBaselined { get { return _testCmd != null; } }

            /// <summary>
            /// A path wrapper to a "raw" file. This will not delete or reinitialize this file.
            /// </summary>
            public OutputPath(string path)
            {
                Path = path;
            }

            /// <summary>
            /// Constructs a new instance. Note that this will attempt to delete the file at
            /// the output directory, assuming any exists.
            /// </summary>
            public OutputPath(TestCommandBase test, string dir, string name)
            {
                Contracts.AssertValue(test);
                Contracts.AssertValue(dir);
                Contracts.AssertValue(name);

                _testCmd = test;
                _dir = dir;
                _name = name;
                Path = _testCmd.DeleteOutputPath(_dir, _name);
            }

            public bool CheckEquality(int digitsOfPrecision = DigitsOfPrecision, NumberParseOption parseOption = NumberParseOption.Default)
            {
                Contracts.Assert(CanBeBaselined);
                return _testCmd.CheckEquality(_dir, _name, digitsOfPrecision: digitsOfPrecision, parseOption: parseOption);
            }

            public bool CheckEqualityNormalized(int digitsOfPrecision = DigitsOfPrecision, NumberParseOption parseOption = NumberParseOption.Default)
            {
                Contracts.Assert(CanBeBaselined);
                return _testCmd.CheckEqualityNormalized(_dir, _name, digitsOfPrecision: digitsOfPrecision, parseOption: parseOption);
            }

            public string ArgStr(string name)
            {
                Contracts.AssertNonEmpty(name);
                return string.Format("{0}={{{1}}}", name, Path);
            }

            private static string[] Append(string one, string[] many)
            {
                if (Utils.Size(many) == 0)
                    return new string[] { one };
                string[] retval = new string[many.Length + 1];
                retval[0] = one;
                many.CopyTo(retval, 1);
                return retval;
            }

            /// <summary>Convenience to express this as a <see cref="PathArgument"/> that should be used as
            /// an argument only, with no comparison.</summary>
            public PathArgument ArgOnly(string name, params string[] names)
            {
                return new PathArgument(this, PathArgument.Usage.Both, PathArgument.Usage.None, false, Append(name, names));
            }

            /// <summary>Convenience to express this as a <see cref="PathArgument"/> that should be used only
            /// for a non-normalized comparison, but not automatically inserted as an argument.</summary>
            public PathArgument ArgNone()
            {
                Contracts.Assert(CanBeBaselined);
                return new PathArgument(this, PathArgument.Usage.None, PathArgument.Usage.Both, false);
            }

            /// <summary>Convenience to express this as a <see cref="PathArgument"/> that should be used for
            /// both automatic insertion as an argument, as well as a non-normalized comparison.</summary>
            public PathArgument Arg(string name, params string[] names)
            {
                Contracts.Assert(CanBeBaselined);
                return new PathArgument(this, PathArgument.Usage.Both, PathArgument.Usage.Both, false, Append(name, names));
            }

            /// <summary>Convenience to express this as a <see cref="PathArgument"/> that should be used for
            /// both automatic insertion as an argument, as well as a normalized comparison.</summary>
            public PathArgument ArgNorm(string name, params string[] names)
            {
                Contracts.Assert(CanBeBaselined);
                return new PathArgument(this, PathArgument.Usage.Both, PathArgument.Usage.Both, true, Append(name, names));
            }
        }

        /// <summary>
        /// This contains a convenience class for capturing the very common test case where
        /// one wants to first incorporate a path as an argument to a command, and then
        /// immediately turn around and run some sort of comparison based on it. It is sort
        /// of a decorated path that describes how the TestCore and similar
        /// methods should incorporate it into the arguments.
        /// </summary>
        protected sealed class PathArgument
        {
            [Flags]
            public enum Usage : byte
            {
                /// <summary>
                /// This indicates that the argument should not actually be automatically
                /// added, and only comparisons should be done.</summary>
                None = 0x0,
                /// <summary>
                /// Argument will be automatically appended to the loader arguments, that
                /// is, those arguments that are only specified when we are not ingesting
                /// a data model.
                /// </summary>
                Loader = 0x1,
                /// <summary>
                /// Argument will be automatically appended to the arguments appended when
                /// loading a data model from a file, and will be omitted otherwise.
                /// </summary>
                DataModel = 0x2,
                /// <summary>
                /// Argument will be automatically appended to all arguments, whether we
                /// are ingesting a data model or not.
                /// </summary>
                Both = 0x3
            }

            public readonly OutputPath Path;
            /// <summary>When the path should be inserted into the arguments.</summary>
            public readonly Usage ArgUsage;
            /// <summary>When the path should be subject to a baseline comparison.</summary>
            public readonly Usage CmpUsage;
            /// <summary>Whether that baseline comparison should be a normalized comparison.</summary>
            public readonly bool Normalized;
            private readonly string[] _names;
            private string _argString;

            public string ArgString
            {
                get
                {
                    if (ArgUsage == Usage.None)
                        return null;
                    if (_argString == null)
                    {
                        var argString = Path.ArgStr(_names[_names.Length - 1]);
                        for (int i = _names.Length - 1; --i >= 0;)
                            argString = string.Format("{0}{{{1}}}", _names[i], argString);
                        _argString = argString;
                    }
                    return _argString;
                }
            }

            /// <summary>
            /// Constructs a path argument explicitly.
            /// </summary>
            /// <param name="argUsage">Indicates to the test method in what broad situation
            /// this path should be auto-appended as an argument</param>
            /// <param name="cmpUsage">Indicates to the test method in what broad situation
            /// this path should be subjected to a baseline comparison</param>
            /// <param name="normalized">Whether this is intended to be a normalized comparison</param>
            /// <param name="names">The arguments names. Normally this will be a single value, but if
            /// it were a subcomponent, you would list the names of the subcomponent with the last name
            /// being the actual path argument name. So if you wanted to have the argument be <c>a{b=path}</c>,
            /// the names would be <c>"a","b"</c>.
            /// </param>
            public PathArgument(OutputPath path, Usage argUsage, Usage cmpUsage, bool normalized, params string[] names)
            {
                Contracts.AssertValue(path);
                // The names should be empty iff usage is none.
                Contracts.Assert((argUsage == Usage.None) != (Utils.Size(names) >= 1));
                Path = path;
                ArgUsage = argUsage;
                CmpUsage = cmpUsage;
                Normalized = normalized;
                _names = names;
            }

            public bool CheckEquality(int digitsOfPrecision = DigitsOfPrecision, NumberParseOption parseOption = NumberParseOption.Default)
            {
                if (Normalized)
                    return Path.CheckEqualityNormalized(digitsOfPrecision, parseOption);
                return Path.CheckEquality(digitsOfPrecision, parseOption);
            }
        }

        protected abstract class RunContextBase
        {
            public readonly TestCommandBase Test;
            public readonly string BaselineDir;
            public readonly string BaselineNamePrefix;

            // Whether to write the progress to the output console file or not.
            public readonly bool BaselineProgress;

            public virtual bool NoComparisons { get { return false; } }

            public RunContextBase(TestCommandBase test, string dir, string namePrefix, bool baselineProgress)
            {
                Contracts.AssertValue(test);
                Contracts.AssertValue(dir);
                Contracts.AssertValue(namePrefix);

                Test = test;
                BaselineDir = dir;
                BaselineNamePrefix = namePrefix;
                BaselineProgress = baselineProgress;
            }

            public OutputPath InitPath(string suffix)
            {
                Contracts.AssertValue(suffix);
                if (char.IsLetterOrDigit(suffix.FirstOrDefault()))
                    suffix = "-" + suffix;
                string name = BaselineNamePrefix + suffix;
                return new OutputPath(Test, BaselineDir, name);
            }

            public virtual OutputPath StdoutPath()
            {
                return InitPath("out.txt");
            }

            public virtual OutputPath ModelPath()
            {
                return InitPath("model.zip");
            }

            public virtual OutputPath FoldModelPath(int fold)
            {
                return InitPath(string.Format("model.fold{0:000}.zip", fold));
            }

            public virtual OutputPath MetricsPath()
            {
                return InitPath("metrics.txt");
            }
        }

        protected virtual void InitializeEnvironment(IHostEnvironment environment)
        {
            environment.AddStandardComponents();
        }

        protected bool TestCore(RunContextBase ctx, string cmdName, string args, params PathArgument[] toCompare)
        {
            return TestCore(ctx, cmdName, args, DigitsOfPrecision, NumberParseOption.Default, toCompare);
        }

        /// <summary>
        /// Runs a command with some arguments. Note that the input
        /// <paramref name="toCompare"/> objects are used for comparison only.
        /// </summary>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestCore(RunContextBase ctx, string cmdName, string args, int digitsOfPrecision, NumberParseOption parseOption, params PathArgument[] toCompare)
        {
            Contracts.AssertValue(cmdName);
            Contracts.AssertValueOrNull(args);
            OutputPath outputPath = ctx.StdoutPath();
            using (var newWriter = OpenWriter(outputPath.Path))
            using (_env.RedirectChannelOutput(newWriter, newWriter))
            {
                _env.ResetProgressChannel();
                int res = MainForTest(_env, newWriter, string.Format("{0} {1}", cmdName, args), ctx.BaselineProgress);
                if (res != 0)
                    Log("*** Predictor returned {0}", res);
            }

            bool all = true;
            if (!ctx.NoComparisons)
            {
                all &= outputPath.CheckEqualityNormalized(digitsOfPrecision, parseOption);
                if (toCompare != null)
                    foreach (var c in toCompare)
                        all &= c.CheckEquality(digitsOfPrecision, parseOption);
            }
            return all;
        }

        /// <summary>
        /// Invoke MAML with specified arguments. This is intended to be used for testing.
        /// The progress is not displayed interactively, but if <paramref name="printProgress"/> is true,
        /// the log is attached at the end.
        /// </summary>
        /// <param name="env">The environment to use.</param>
        /// <param name="writer">
        /// The writer to print the <see cref="BaseTestBaseline.ProgressLogLine"/>. Usually this should be the same writer that is used in <paramref name="env"/>.
        /// </param>
        /// <param name="args">The arguments for MAML.</param>
        /// <param name="printProgress">Whether to print the progress summary. If true, progress summary will appear in the end of baseline output file.</param>
        private protected static int MainForTest(ConsoleEnvironment env, TextWriter writer, string args, bool printProgress = false)
        {
            Contracts.AssertValue(env);
            Contracts.AssertValue(writer);
            int result = Maml.MainCore(env, args, false);
            if (printProgress)
            {
                writer.WriteLine(ProgressLogLine);
                env.PrintProgress();
            }
            return result;
        }

        /// <summary>
        /// Checks that <paramref name="testOutPath"/>'s contents are a suffix of <paramref name="trainTestOutPath"/>'s
        /// contents, assuming one skips <paramref name="skip"/> lines from <paramref name="testOutPath"/>.
        /// </summary>
        protected bool CheckTestOutputMatchesTrainTest(string trainTestOutPath, string testOutPath, int skip = 0)
        {
            Contracts.Assert(skip >= 0);

            // Normalize the output file.
            Normalize(testOutPath);
            bool res = CheckOutputIsSuffix(trainTestOutPath, testOutPath, skip, ProgressLogLine);
            if (res)
                File.Delete(testOutPath + RawSuffix);
            return res;
        }
    }

    /// <summary>
    /// A test class with some convenience methods for those commands that involve
    /// the saving or loading of data models.
    /// </summary>
    public abstract partial class TestDmCommandBase : TestCommandBase
    {
        private bool TestCoreCore(RunContextBase ctx, string cmdName, string dataPath, PathArgument.Usage situation,
            OutputPath inModelPath, OutputPath outModelPath, string loaderArgs, string extraArgs, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, situation, inModelPath, outModelPath, loaderArgs, extraArgs, DigitsOfPrecision, NumberParseOption.Default, toCompare);
        }

        private bool TestCoreCore(RunContextBase ctx, string cmdName, string dataPath, PathArgument.Usage situation,
            OutputPath inModelPath, OutputPath outModelPath, string loaderArgs, string extraArgs, int digitsOfPrecision,
            NumberParseOption parseOption, params PathArgument[] toCompare)
        {
            Contracts.AssertNonEmpty(cmdName);
            Contracts.AssertValueOrNull(dataPath);
            Contracts.AssertValueOrNull(inModelPath);
            Contracts.AssertValueOrNull(outModelPath);
            Contracts.AssertValueOrNull(loaderArgs);
            Contracts.AssertValueOrNull(extraArgs);
            Contracts.Assert(Utils.Size(toCompare) == 0 || toCompare.All(x => x != null));

            // Construct the arguments for the version of the command where we will
            // create then save the data model file.
            List<string> args = new List<string>();
            if (!string.IsNullOrWhiteSpace(dataPath))
                args.Add(string.Format("data={{{0}}}", dataPath));
            if (!string.IsNullOrWhiteSpace(loaderArgs))
                args.Add(loaderArgs);
            foreach (var pa in toCompare)
            {
                if ((pa.ArgUsage & situation) != PathArgument.Usage.None)
                    args.Add(pa.ArgString);
            }
            if (inModelPath != null)
                args.Add(inModelPath.ArgStr("in"));
            if (outModelPath != null)
                args.Add(outModelPath.ArgStr("out"));
            if (!string.IsNullOrWhiteSpace(extraArgs))
                args.Add(extraArgs);
            var argString = string.Join(" ", args);
            var paths = toCompare.Where(pa => (pa.CmpUsage & situation) != PathArgument.Usage.None).ToArray();
            return TestCore(ctx, cmdName, argString, digitsOfPrecision: digitsOfPrecision, parseOption: parseOption, toCompare: paths);
        }

        /// <summary>
        /// Run a single command, with the loader arguments, saving an output data model.
        /// </summary>
        /// <param name="ctx">The run context from which we can generate our output paths</param>
        /// <param name="cmdName">The loadname of the <see cref="ICommand"/></param>
        /// <param name="dataPath">The path to the input data</param>
        /// <param name="loaderArgs">The arguments that get passed only to the loader version
        /// of the command, typically only the loader arguments. Note that convenience arguments
        /// like <c>xf</c> that become part of a loader would also fit in this category.</param>
        /// <param name="extraArgs">Extra arguments passed to both loader/datamodel versions
        /// of the command</param>
        /// <param name="toCompare">Extra output paths to compare to baseline, besides the
        /// stdout</param>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestCore(RunContextBase ctx, string cmdName, string dataPath, string loaderArgs, string extraArgs, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.DataModel, null, ctx.ModelPath(), loaderArgs, extraArgs, toCompare);
        }

        protected bool TestCore(RunContextBase ctx, string cmdName, string dataPath, string loaderArgs, string extraArgs, int digitsOfPrecision, NumberParseOption parseOption, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.DataModel, null, ctx.ModelPath(), loaderArgs, extraArgs, digitsOfPrecision, parseOption, toCompare);
        }

        /// <summary>
        /// Run one command loading the datafile loaded as defined by a model file, and comparing
        /// against standard output. This utility method will load a model, but not save a model.
        /// </summary>
        /// <param name="ctx">The run context from which we can generate our output paths</param>
        /// <param name="cmdName">The loadname of the <see cref="ICommand"/></param>
        /// <param name="dataPath">The path to the input data</param>
        /// <param name="modelPath">The model to load</param>
        /// <param name="extraArgs">Arguments passed to the command</param>
        /// <param name="toCompare">Extra output paths to compare to baseline, besides the
        /// stdout</param>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestInCore(RunContextBase ctx, string cmdName, string dataPath, OutputPath modelPath, string extraArgs, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.Loader, modelPath, null, null, extraArgs, toCompare);
        }

        protected bool TestInCore(RunContextBase ctx, string cmdName, string dataPath, OutputPath modelPath, string extraArgs, int digitsOfPrecision = DigitsOfPrecision, NumberParseOption parseOption = NumberParseOption.Default, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.Loader, modelPath, null, null, extraArgs, digitsOfPrecision, parseOption, toCompare);
        }

        /// <summary>
        /// Run one command loading the datafile loaded as defined by a model file, and comparing
        /// against standard output. This utility method will both load and save a model.
        /// </summary>
        /// <param name="ctx">The run context from which we can generate our output paths</param>
        /// <param name="cmdName">The loadname of the <see cref="ICommand"/></param>
        /// <param name="dataPath">The path to the input data</param>
        /// <param name="modelPath">The model to load</param>
        /// <param name="extraArgs">Arguments passed to the command</param>
        /// <param name="toCompare">Extra output paths to compare to baseline, besides the
        /// stdout</param>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestInOutCore(RunContextBase ctx, string cmdName, string dataPath, OutputPath modelPath, string extraArgs, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.Both, modelPath, ctx.ModelPath(), null, extraArgs, toCompare);
        }

        /// <summary>
        /// Run one command loading the datafile loaded as defined by a model file, and comparing
        /// against standard output. This utility method will both load and save a model.
        /// </summary>
        /// <param name="ctx">The run context from which we can generate our output paths</param>
        /// <param name="cmdName">The loadname of the <see cref="ICommand"/></param>
        /// <param name="dataPath">The path to the input data</param>
        /// <param name="modelPath">The model to load</param>
        /// <param name="extraArgs">Arguments passed to the command</param>
        /// <param name="toCompare">Extra output paths to compare to baseline, besides the
        /// stdout</param>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestInOutCore(RunContextBase ctx, string cmdName, string dataPath, OutputPath modelPath, string extraArgs, int digitsOfPrecision = DigitsOfPrecision, NumberParseOption parseOption = NumberParseOption.Default, params PathArgument[] toCompare)
        {
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.Both, modelPath, ctx.ModelPath(), null, extraArgs, digitsOfPrecision, parseOption, toCompare);
        }

        /// <summary>
        /// Run two commands, one with the loader arguments, the second with the loader loaded
        /// from the data model, ensuring that stdout is the same compared to baseline in both cases.
        /// </summary>
        /// <param name="ctx">The run context from which we can generate our output paths</param>
        /// <param name="cmdName">The loadname of the <see cref="ICommand"/></param>
        /// <param name="dataPath">The path to the input data</param>
        /// <param name="loaderArgs">The arguments that get passed only to the loader version
        /// of the command, typically only the loader arguments. Note that convenience arguments
        /// like <c>xf</c> that become part of a loader would also fit in this category. Any
        /// arguments that you would not want specified when loading the data model from a file
        /// must go here!</param>
        /// <param name="extraArgs">Extra arguments passed to both loader/datamodel versions
        /// of the command</param>
        /// <param name="dmArgs">The arguments that get passed to only the datamodel version
        /// of the command</param>
        /// <param name="toCompare">Extra output paths to compare to baseline, besides the
        /// stdout</param>
        /// <returns>Whether this test succeeded.</returns>
        protected bool TestReloadedCore(RunContextBase ctx, string cmdName, string dataPath, string loaderArgs, string extraArgs,
            string dmArgs, params PathArgument[] toCompare)
        {
            Contracts.AssertValue(ctx);
            Contracts.AssertValue(cmdName);
            Contracts.AssertValue(dataPath);

            OutputPath dmPath = ctx.ModelPath();

            // Only run the reloading if the first test succeeded. Otherwise we'll obscure the proximate cause of the failure.
            return TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.DataModel, null, dmPath, loaderArgs, extraArgs, toCompare)
                && TestCoreCore(ctx, cmdName, dataPath, PathArgument.Usage.Loader, dmPath, null, null, extraArgs, toCompare);
        }

        /// <summary>
        /// Utility method utilized by the test methods to produce the <c>data</c>
        /// argument, assuming that one should exist.
        /// </summary>
        private string DataArg(string dataPath)
        {
            Contracts.AssertValueOrNull(dataPath);
            if (string.IsNullOrWhiteSpace(dataPath))
                return "";
            return string.Format("data={{{0}}}", dataPath);
        }

        protected void TestPipeFromModel(string dataPath, OutputPath model)
        {
            var env = new MLContext(seed: 42);
            var files = new MultiFileSource(dataPath);

            bool tmp;
            IDataView pipe;
            using (var file = Env.OpenInputFile(model.Path))
            using (var strm = file.OpenReadStream())
            using (var rep = RepositoryReader.Open(strm, env))
            {
                ModelLoadContext.LoadModel<IDataView, SignatureLoadDataLoader>(env,
                    out pipe, rep, ModelFileUtils.DirDataLoaderModel, files);
            }

            using (var c = pipe.GetRowCursorForAllColumns())
                tmp = CheckSameValues(c, pipe, true, true, true);
            Check(tmp, "Single value same failed");
        }
    }

    public abstract class TestSteppedDmCommandBase : TestDmCommandBase
    {
        protected TestSteppedDmCommandBase(ITestOutputHelper helper) : base(helper)
        {
            _step = 0;
            _paramsStep = -1;
            _params = null;
        }

        /// <summary>
        /// Multiple commands we sometimes expect to produce the same stdout,
        /// for example, rerunning. If you want to move on to another stdout comparison
        /// file, increment this.
        /// </summary>
        protected int _step;

        private int _paramsStep;
        private RunContextBase _params;

        protected RunContextBase Params
        {
            get
            {
                EnsureRunParams();
                return _params;
            }
        }

        private sealed class RunParamaters : RunContextBase
        {
            public RunParamaters(TestCommandBase test)
                : base(test, Cat, test.TestName, false)
            {
            }

            public RunParamaters(TestCommandBase test, int step)
                : base(test, Cat, step == 0 ? test.TestName : string.Format("{0}-{1}", test.TestName, step), false)
            {
            }
        }

        private void EnsureRunParams()
        {
            if (_step == _paramsStep && _params != null)
                return;
            _params = new RunParamaters(this, _step);
            _paramsStep = _step;
        }

        /// <summary>
        /// Generates a canonical output path for stdout captures.
        /// </summary>
        /// <returns></returns>
        protected OutputPath StdoutPath()
        {
            return Params.StdoutPath();
        }

        /// <summary>
        /// Generates a canonical output path for per-instance output metrics.
        /// </summary>
        /// <returns></returns>
        protected OutputPath MetricsPath()
        {
            return Params.MetricsPath();
        }

        /// <summary>
        /// Returns the current model path for this step. This can be called multiple times
        /// if a test wants to access the data model path from a run for its own purposes.
        /// </summary>
        protected OutputPath ModelPath()
        {
            // REVIEW: Should at some point probably just call this "model," as we also
            // have tests using this for saving the predictor model, etc. etc. Also make the index
            // consistent with the step above.
            return Params.ModelPath();
        }

        /// <summary>
        /// Returns the current model path for fold for this step.
        /// </summary>
        protected OutputPath FoldModelPath(int fold)
        {
            return Params.FoldModelPath(fold);
        }

        /// <summary>
        /// Creates an output path with a suffix based on the test name. For new tests please
        /// do not use this, but instead utilize the <see cref="TestCommandBase.RunContextBase.InitPath"/>
        /// method.
        /// </summary>
        protected OutputPath CreateOutputPath(string suffix)
        {
            // REVIEW: When convenient move the baseline files, and use Params.InitPath.
            Contracts.AssertValue(suffix);
            if (char.IsLetterOrDigit(suffix.FirstOrDefault()))
                suffix = '-' + suffix;
            return new OutputPath(this, Params.BaselineDir, TestName + suffix);
        }

        protected bool TestCore(string cmdName, string dataPath, string loaderArgs, string extraArgs, params PathArgument[] toCompare)
        {
            return TestCore(Params, cmdName, dataPath, loaderArgs, extraArgs, DigitsOfPrecision, NumberParseOption.Default, toCompare);
        }

        protected bool TestCore(string cmdName, string dataPath, string loaderArgs, string extraArgs, int digitsOfPrecision, params PathArgument[] toCompare)
        {
            return TestCore(Params, cmdName, dataPath, loaderArgs, extraArgs, digitsOfPrecision, NumberParseOption.Default, toCompare);
        }

        protected bool TestCore(string cmdName, string args, params PathArgument[] toCompare)
        {
            return TestCore(Params, cmdName, args, toCompare);
        }

        protected bool TestReloadedCore(string cmdName, string dataPath, string loaderArgs, string extraArgs,
            string dmArgs, params PathArgument[] toCompare)
        {
            return TestReloadedCore(Params, cmdName, dataPath, loaderArgs, extraArgs, dmArgs, toCompare);
        }

        protected bool TestInCore(string cmdName, string dataPath, OutputPath modelPath, string extraArgs, params PathArgument[] toCompare)
        {
            return TestInCore(Params, cmdName, dataPath, modelPath, extraArgs, toCompare);
        }

        protected bool TestInCore(string cmdName, string dataPath, OutputPath modelPath, string extraArgs, int digitsOfPrecision = DigitsOfPrecision, params PathArgument[] toCompare)
        {
            return TestInCore(Params, cmdName, dataPath, modelPath, extraArgs, digitsOfPrecision, NumberParseOption.Default, toCompare);
        }

        protected bool TestInOutCore(string cmdName, string dataPath, OutputPath modelPath, string extraArgs, params PathArgument[] toCompare)
        {
            return TestInOutCore(Params, cmdName, dataPath, modelPath, extraArgs, toCompare);
        }

        protected bool TestInOutCore(string cmdName, string dataPath, OutputPath modelPath, string extraArgs, int digitsOfPrecision = DigitsOfPrecision, params PathArgument[] toCompare)
        {
            return TestInOutCore(Params, cmdName, dataPath, modelPath, extraArgs, digitsOfPrecision, NumberParseOption.Default, toCompare);
        }
    }

    // REVIEW: This class doesn't really belong in a file called TestCommandBase.
    //                 And the name of this class isn't real suggestive or accurate.
    public sealed partial class TestDmCommand : TestSteppedDmCommandBase
    {
        // REVIEW: Migrate existing tests where the train/score/evaluate runs
        // are explicit in favor of the more generic tests where appropriate.

        [TestCategory(Cat)]
        [Fact]
        public void EvaluateRankingWithMaml()
        {
            RunMTAThread(() =>
            {
                string trainData = GetDataPath("adult.tiny.with-schema.txt");
                string extraArgs = $"tr=FastRankRanking{{t=1}} eval=RankingEvaluator{{t=10}} prexf=rangefilter{{col=Label min=20 max=25}} " +
                $"prexf=term{{col=Strat:Label}} xf=term{{col=Label}} xf=hash{{col=GroupId}} threads- norm=Warn";

                string loaderArgs = "loader=text{col=Features:R4:10-14 col=Label:R4:9 col=GroupId:TX:1 header+}";

                TestCore("cv", trainData, loaderArgs, extraArgs);
            });
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandShowSchema()
        {
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            TestReloadedCore("showschema", pathData, "loader=text{header+}", "slots+", null);
            Done();
        }

        [TestCategory(Cat)]
        [Fact]
        public void CommandShowSchemaModel()
        {
            string trainDataPath = GetDataPath("adult.tiny.with-schema.txt");
            string modelPath = ModelPath().Path;
            string args =
                string.Format(
                    @"train data={{{0}}}
                     loader=Text{{
                        header=+
                        col=NumFeatures:Num:9-14
                        col=CatFeaturesText:TX:0~*
                        col=Label:Num:0}}
                    xf=Categorical{{col=CatFeatures:CatFeaturesText}}
                    xf=Concat{{col=Features:NumFeatures,CatFeatures}}
                    trainer=ft{{numberOfTrees=1 numberOfLeaves=2}}
                    out={{{1}}}", trainDataPath, modelPath);
            RunMTAThread(new ThreadStart(() => MainForTest(args)));
            TestCore("showschema", string.Format("steps+ in={{{0}}} meta+", modelPath));
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandShowDataTextSlotNames()
        {
            // First create the text loader data model.
            var path = CreateOutputPath("DataA.txt");
            File.WriteAllLines(path.Path, new string[] {
                "Zeus,Hera,Athena,Artemis,Hephaestus,Eos",
                "0,1,2,3,4,5"
            });
            var modelPath = ModelPath();
            TestCore("showdata", path.Path, "loader=text{col=A:I4:0-1 col=B:I4:~ header+ sep=comma}", "");

            // The slot names should have been retained in the data model somehow.
            _step++;
            TestInCore("showdata", null, modelPath, "");

            // If a new file is specified, the intent is that header will "override" the
            // stored header. This will extend to the new output data model. Also incidentally
            // check whether ~ is "materialized" into the specific range of slot indices 2-5.
            _step++;
            var path2 = CreateOutputPath("DataB.txt");
            File.WriteAllLines(path2.Path, new string[] {
                "Jupiter,Juno,Minerva,Diana,Vulcan,Aurora,IShouldNotBeHere",
                "6,7,8,9,10,11,12"
            });
            var modelPath2 = ModelPath();
            TestInOutCore("showdata", path2.Path, modelPath, "");

            // Ensure that the new data model has picked up the new slot names.
            _step++;
            TestInCore("showdata", null, modelPath2, "");

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandSavePredictor()
        {
            // Train on a file with header
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=text{header+}", "tr=ap{shuf-}");

            // Save the model as text
            _step++;
            OutputPath textModel = CreateOutputPath("model_dense_names.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            // Train on a file without header
            _step++;
            pathData = GetDataPath(@"..", "breast-cancer.txt");
            trainModel = ModelPath();
            TestCore("train", pathData, "", "tr=ap{shuf-}");

            // Save the model as text
            _step++;
            textModel = CreateOutputPath("model_no_names.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            // Train on a file without header, but make the feature names sparse
            _step++;
            pathData = GetDataPath(@"..", "breast-cancer.txt");
            trainModel = ModelPath();
            TestCore("train", pathData, "loader=Text{col=Label:0 col=A:1 col=B:2 col=Rest:3-*}", "xf=Concat{col=Features:A,Rest,B} tr=ap{shuf-}");

            // Save the model as text
            _step++;
            textModel = CreateOutputPath("model_sparse_names.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            // Train on a file without header, but make the feature names almost dense
            _step++;
            pathData = GetDataPath(@"..", "breast-cancer.txt");
            trainModel = ModelPath();
            TestCore("train", pathData, "loader=Text{col=Label:0 col=A:1 col=B:2 col=C:3 col=D:4 col=E:5 col=F:6 col=Rest:7-*}",
                "xf=Concat{col=Features:A,B,C,D,E,F,Rest} tr=ap{shuf-}");

            // Save the model as text
            _step++;
            textModel = CreateOutputPath("model_almost_dense.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            // Train OneClassSvm anomaly detector
            _step++;
            pathData = GetDataPath(@"..", "breast-cancer.txt");
            trainModel = ModelPath();
            TestCore("train", pathData, "loader=Text{col=Features:1-*}", "tr=OneClassSvm{}");

            // Save the model as text
            _step++;
            textModel = CreateOutputPath("model_one_class_svm.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            //Train empty lr model without weights
            _step++;
            pathData = GetDataPath(@"..", "data-for-empty-predictor.txt");
            trainModel = ModelPath();
            TestCore("train", pathData, "loader=TextLoader{col=Label:R4:0 col=Features:R4:1-2 header=+}", "tr=LogisticRegression {l2=0 l1=1000}");

            //Save the model as text
            _step++;
            textModel = CreateOutputPath("lr_without_weights.txt");
            TestInCore("savepredictor", null, trainModel, null, textModel.Arg("text"));

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void SaveMulticlassLrPredictorAsSummary()
        {
            // First run a training.
            string pathData = GetDataPath("iris.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=TextLoader{col={name=Label src=0} col={name=Features src=1-4}}",
                "lab=Label feat=Features seed=42 tr=mlr{nt=1 l1=0.1 maxiter=70} norm=Warn");

            // Save the model summary
            _step++;
            OutputPath summaryModel = CreateOutputPath("summary.txt");
            TestInCore("savepredictor", null, trainModel, null, summaryModel.Arg("sum"));

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandCrossValidation()
        {
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            var summaryFile = CreateOutputPath("summary.txt");
            var prFile = CreateOutputPath("pr.txt");
            var metricsFile = MetricsPath();
            // Use a custom name for the label and features to ensure that they are communicated to the scorer and evaluator.
            const string extraArgs = "eval=bin lab=Lab feat=Feat tr=lr{t-} threads- opf+ norm=Warn";
            // Need a transform that produces the label and features column to ensure that the columns are resolved appropriately.
            const string loaderArgs = "loader=text{header+ col=L:0 col=F:1-*} xf=Copy{col=Lab:L col=Feat:F}";
            TestCore("cv", pathData, loaderArgs, extraArgs, summaryFile.Arg("sf"), prFile.Arg("eval", "pr"), metricsFile.Arg("dout"));
            Done();
        }

        [Fact]
        public void CommandCrossValidationKeyLabelWithFloatKeyValues()
        {
            RunMTAThread(() =>
            {
                string pathData = GetDataPath(@"adult.tiny.with-schema.txt");
                var perInstFile = CreateOutputPath("perinst.txt");
                // Create a copy of the label column and use it for stratification, in order to create different label counts in the different folds.
                string extraArgs = $"tr=FastRankRanking{{t=1}} strat=Strat prexf=rangefilter{{col=Label min=20 max=25}} prexf=term{{col=Strat:Label}} xf=term{{col=Label}} xf=hash{{col=GroupId}} threads- norm=Warn dout={{{perInstFile.Path}}}";
                string loaderArgs = "loader=text{col=Features:R4:10-14 col=Label:R4:9 col=GroupId:TX:1 header+}";
                TestCore("cv", pathData, loaderArgs, extraArgs);
            });
            Done();
        }

        [Fact]
        public void CommandCrossValidationWithTextStratificationColumn()
        {
            string pathData = GetDataPath(@"adult.tiny.with-schema.txt");
            string extraArgs = $"tr=lr{{{TestLearnersBase.logisticRegression.Trainer.SubComponentSettings}}} strat=Strat threads- norm=Warn";
            string loaderArgs = "loader=text{col=Features:R4:9-14 col=Label:R4:0 col=Strat:TX:1 header+}";
            TestCore("cv", pathData, loaderArgs, extraArgs, 5);
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandCrossValidationVectorNoNames()
        {
            string pathData = GetDataPath(@"..", "breast-cancer.txt");
            var metricsFile = MetricsPath();
            // Load breast-cancer.txt with a vector valued but no slot names Name column,
            // to test whether cross validation handles that correctly.
            const string extraArgs = "tr=lr{t-} threads-";
            const string loaderArgs = "loader=text{col=Label:0 col=Features:1-* col=Name:0-1}";
            TestCore("cv", pathData, loaderArgs, extraArgs, metricsFile.Arg("dout"));
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandCrossValidationFCC()
        {
            string pathData = GetDataPath("adult.tiny.with-schema.txt");
            var metricsFile = MetricsPath();
            const string loaderArgs = "loader=TextLoader{col=Features:R4:9-14 col=workclass:TX:1 col=education:TX:2 col=marital_status:TX:3 " +
                " col=occupation:TX:4 col=relationship:TX:5 col=ethnicity:TX:6 col=sex:TX:7 col=native_country:TX:8 col=label_IsOver50K_:R4:0 header=+} " +
                " xf=CopyColumns{col=Label:label_IsOver50K_} xf=CategoricalTransform{col=workclass col=education col=marital_status col=occupation col=relationship col=ethnicity col=sex col=native_country} " +
                " xf=Concat{col=Features:Features,workclass,education,marital_status,occupation,relationship,ethnicity,sex,native_country}" +
                " prexf=Take{c=100}";

            const string extraArgs = "tr=ap{shuf-} threads- norm=Yes scorer=fcc";

            var f1 = Params.InitPath("metrics.fold000.txt");
            var f2 = Params.InitPath("metrics.fold001.txt");

            TestCore("cv", pathData, loaderArgs, extraArgs + " collate-", metricsFile.ArgOnly("dout"), f1.ArgNone(), f2.ArgNone());

            _step++;

            TestCore("cv", pathData, loaderArgs, extraArgs, metricsFile.Arg("dout"));

            Done();
        }

        [TestCategory(Cat), TestCategory("Multiclass")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandCrossValidationAndSave()
        {
            const int numFolds = 2;
            string pathData = GetDataPath(@"..\V3\Data\20NewsGroups", "all-data_small.txt");
            OutputPath trainModel = ModelPath();
            OutputPath[] foldModels = new OutputPath[numFolds];
            for (int i = 0; i < numFolds; i++)
                foldModels[i] = FoldModelPath(i);

            string extraArgs = string.Format("{0} {1} {2} {3} k={4}", "prexf=Term{col=Label:Cat} prexf=CategoricalTransform{col=Cat01}",
                                                "xf=TextTransform{col=Text} xf=Concat{col=Features:Cat01,Text}",
                                                "threads- tr=MulticlassLogisticRegression{numThreads=1}", "norm=No", numFolds);
            const string loaderArgs = "loader=TextLoader{col=Label:R4:0 col=Cat:TX:1 col=Cat01:TX:2 col=Text:TX:3 header=+}";
            TestCore("cv", pathData, loaderArgs, extraArgs);

            for (int i = 0; i < numFolds; i++)
            {
                _step++;
                TestInCore("test", pathData, foldModels[i], "");
            }
            Done();
        }

        // Purpose of this test is to validate what our code correctly handle situation with
        // multiple different FastTree (Ranking and Classification for example) instances in different threads.
        // FastTree internally fails if we try to run it simultaneously and if this happens we wouldn't get model file for training.
        [TestCategory(Cat)]
        [Fact]
        public async Task CommandTrainFastTreeInDifferentThreads()
        {
            var dataPath = GetDataPath(TestDatasets.adult.testFilename);
            var firstModelOutPath = DeleteOutputPath("TreeTransform-model2.zip");
            var secondModelOutPath = DeleteOutputPath("TreeTransform-model1.zip");
            var trainArgs = $"Train tr=SDCA {TestDatasets.adult.loaderSettings} {TestDatasets.adult.mamlExtraSettings[0]} {TestDatasets.adult.mamlExtraSettings[1]}" +
                " xf=TreeFeat{tr=FastTreeBinaryClassification} xf=TreeFeat{tr=FastTreeRanking} xf=Concat{col=Features:Features,Leaves,Paths,Trees}";

            var firsttrainArgs = $"{trainArgs} data={dataPath} out={firstModelOutPath}";
            var secondTrainArgs = $"{trainArgs} data={dataPath} out={secondModelOutPath}";

            var t = new Task<int>[2];
            t[0] = new Task<int>(() => MainForTest(firsttrainArgs));
            t[1] = new Task<int>(() => MainForTest(secondTrainArgs));
            t[0].Start();
            t[1].Start();
            var t0 = await t[0];
            var t1 = await t[1];

            Assert.Equal(0, t0);
            Assert.Equal(0, t1);
        }

        [TestCategory(Cat), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainTest()
        {
            RunMTAThread(() =>
            {
                string trainData = GetDataPath("adult.tiny.with-schema.txt");
                string testData = GetDataPath(@"..\UCI", "adult.test.tiny");
                const string loaderArgs = "loader=text{header+ col=Lab:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9} "
                    + "xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Feat:Cat,Num}";
                OutputPath metricsFile = MetricsPath();
                string extraArgs = string.Format("test={{{0}}} tr=ft{{t=1}} lab=Lab feat=Feat norm=Warn", testData);
                TestCore("traintest", trainData, loaderArgs, extraArgs, metricsFile.Arg("dout"));
            });
            Done();
        }

        [TestCategory(Cat), TestCategory("FastTree"), TestCategory("Transposer")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainTestTranspose()
        {
            string trainData = GetDataPath("adult.tiny.with-schema.txt");
            string testData = GetDataPath("adult.tiny.with-schema.txt");
            const string loaderArgs = "loader=text{header+ col=Label:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9} "
                + "xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Features:Cat,Num}";

            OutputPath transTrainData = CreateOutputPath("adult_train.tdv");
            OutputPath modelPath = ModelPath();
            TestCore("savedata", trainData, loaderArgs, "saver=trans",
                transTrainData.ArgOnly("dout"));
            _step++;
            OutputPath transTestData = CreateOutputPath("adult_test.tdv");
            TestCore("savedata", testData, "", "saver=trans",
                modelPath.ArgOnly("in"), transTestData.ArgOnly("dout"));
            _step++;

            // The resulting output file should be similar to the vanilla train-test above,
            // except for the note about how the data is being prepared, and there is also
            // a possibility of slightly different results on account of different sparsity
            // schemes, though this does not appear to be present here.
            RunMTAThread(() =>
            {
                OutputPath metricsFile = MetricsPath();
                const string extraArgs = "tr=ft{t=1} norm=Warn";
                TestCore("traintest", transTrainData.Path, "loader=trans", extraArgs,
                    metricsFile.Arg("dout"), transTestData.ArgOnly("test"));
            });
            Done();
        }

        [Fact(Skip = "Need CoreTLC specific baseline update")]
        [TestCategory(Cat), TestCategory("FastTree")]
        public void CommandTrainTestWithFunkyFeatureColName()
        {
            RunMTAThread(() =>
            {
                string trainData = GetDataPath("adult.tiny.with-schema.txt");
                string testData = GetDataPath("adult.tiny.with-schema.txt");
                const string featureColumnName = "{Funky \\} feat\\{col name}";
                const string loaderArgs = "loader=text{header+ col=Lab:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9} "
                    + "xf=Cat{col=Cat} xf=Concat{col={name=" + featureColumnName + " src=Cat src=Num}}";
                OutputPath metricsFile = MetricsPath();
                string extraArgs = string.Format("test={{{0}}} tr=ft{{t=1}} lab=Lab feat={1} norm=Yes", testData, featureColumnName);
                TestCore("traintest", trainData, loaderArgs, extraArgs, metricsFile.Arg("dout"));
            });
            Done();
        }

        [TestCategory(Cat), TestCategory("Logistic Regression"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingLrWithInitialization()
        {
            const string loaderArgs = "loader=text{header+ col=Lab:0 col=Num:9-14}";
            const string extraArgs = "tr=lr{t=-} lab=Lab feat=Num";

            string trainData = GetDataPath("adult.tiny.with-schema.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            string moreTrainData = GetDataPath("adult.tiny.with-schema.txt");
            TestInOutCore("train", moreTrainData, trainModel, extraArgs + " " + loaderArgs + " cont+");

            Done();
        }

        private void CommandTrainingLinearLearnerTest(string trArg)
        {
            const string loaderArgs = "loader=text{header+ col=Lab:0 col=Num:9-14}";
            string extraArgs = "tr=" + trArg + " lab=Lab feat=Num";

            string trainData = GetDataPath("adult.tiny.with-schema.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            string moreTrainData = GetDataPath("adult.tiny.with-schema.txt");
            TestInOutCore("train", moreTrainData, trainModel, extraArgs + " cont+");

            // Save model summary.
            _step++;
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));
            Done();
        }

        [TestCategory(Cat), TestCategory("SymSGD"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        //SymSGD seems is not deterministic at times.
        public void CommandTrainingSymSgdWithInitialization() => CommandTrainingLinearLearnerTest("SymSGD{}");

        [TestCategory(Cat), TestCategory("SGD"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingSgdWithInitialization() => CommandTrainingLinearLearnerTest("sgd{nt=1 checkFreq=-1}");

        [TestCategory(Cat), TestCategory("SVM"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingSvmWithInitialization() => CommandTrainingLinearLearnerTest("svm{iter=1}");

        [TestCategory(Cat), TestCategory("AP"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingAvgPerWithInitialization() => CommandTrainingLinearLearnerTest("avgper{}");

        [TestCategory(Cat), TestCategory("OGD"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingOgdWithInitialization() => CommandTrainingLinearLearnerTest("ogd{}");

        [TestCategory(Cat), TestCategory("Logistic Regression")]
        [Fact]
        public void CommandTrainingLrWithStats()
        {
            const string loaderArgs = "loader=text{header+ col=Lab:0 col=Num:9-14}";
            const string extraArgs = "tr=lr{t=- stat=+} lab=Lab feat=Num";

            string trainData = GetDataPath("adult.tiny.with-schema.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs, digitsOfPrecision: 3);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", digitsOfPrecision: 3, modelSummary.Arg("sum"));

            Done();
        }

        [TestCategory(Cat), TestCategory("Logistic Regression")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingLrNonNegative()
        {
            const string loaderArgs = "loader=TextLoader{col=Label:R4:0 col=Features:R4:4-17 header=+}";
            const string extraArgs = "tr=LogisticRegression {l2=0 ot=1E-04 t=- nn=+}";

            string trainData = GetDataPath(@"..\SvmLite", "australian");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));

            Done();
        }

        [TestCategory(Cat), TestCategory("Multiclass"), TestCategory("Logistic Regression"), TestCategory("Continued Training")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingMlrWithInitialization()
        {
            const string loaderArgs = "loader=TextLoader{col=Label:0 col=Features:1-4} ";
            const string extraArgs = "lab=Label feat=Features seed=13 tr=mlr{t=-}";

            string trainData = GetDataPath("iris.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            TestInOutCore("train", trainData, trainModel, extraArgs + " " + loaderArgs + " cont+");

            Done();
        }

        [TestCategory(Cat), TestCategory("Multiclass"), TestCategory("Logistic Regression")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingMlrNonNegative()
        {
            const string loaderArgs = "loader=TextLoader{col=Label:R4:0 col=Features:R4:1-4}";
            const string extraArgs = "tr=LogisticRegression {t=- nn=+}";

            string trainData = GetDataPath("iris.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));

            Done();
        }

        [TestCategory(Cat), TestCategory("Multiclass"), TestCategory("Logistic Regression")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainMlrWithLabelNames()
        {
            // Train MultiLR model.
            const string loaderCmdline = @"loader=TextLoader{col=Label:TX:0 col=Features:R4:1-4 header=+}";
            string pathTrain = GetDataPath("iris-label-name.txt");
            OutputPath trainModel = ModelPath();
            const string trainArgs = "tr=MulticlassLogisticRegression{maxiter=100 t=-} xf=Term{col=Label} seed=1";
            TestCore("train", pathTrain, loaderCmdline, trainArgs);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));

            _step++;
            // Save model as text.
            OutputPath modelText = CreateOutputPath("text.txt");
            TestInCore("savemodel", null, trainModel, "", modelText.Arg("text"));

            Done();
        }

        [Fact]
        [TestCategory(Cat), TestCategory("Multiclass"), TestCategory("Logistic Regression")]
        public void CommandTrainMlrWithStats()
        {
            // Train MultiLR model.
            const string loaderCmdline = @"loader=TextLoader{col=Label:TX:4 col=Features:R4:0-3 sep=,}";
            string pathTrain = GetDataPath("iris.data");
            OutputPath trainModel = ModelPath();
            const string trainArgs = "tr=MultiClassLogisticRegression{maxiter=100 t=- stat=+} xf=Term{col=Label} seed=1";
            TestCore("train", pathTrain, loaderCmdline, trainArgs, 4);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainingPoissonRegNonNegative()
        {
            const string loaderArgs = "loader=Text{col=Label:0 col=Cat3:TX:3 col=Cat4:TX:4 col=Cat5:TX:5 col=Cat6:TX:6 col=Cat7:TX:7 col=Cat8:TX:8 col=Cat9:TX:9 col=Cat15:TX:15 col=Cat16:TX:16 col=Cat18:TX:18 col=Features:~}";
            const string extraArgs = "xf=Cat{col=Cat3 col=Cat4 col=Cat5 col=Cat6 col=Cat7 col=Cat8 col=Cat9 col=Cat15 col=Cat16 col=Cat18} " +
                                     "xf=Concat{col=Features:Features,Cat3,Cat4,Cat5,Cat6,Cat7,Cat8,Cat9,Cat15,Cat16,Cat18}" +
                                     "tr=PoissonRegression {nt=1 nn=+ ot=1e-3}";

            string trainData = GetDataPath("auto-sample.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", trainData, loaderArgs, extraArgs);

            _step++;
            // Save model summary.
            OutputPath modelSummary = CreateOutputPath("summary.txt");
            TestInCore("savemodel", null, trainModel, "", modelSummary.Arg("sum"));

            Done();
        }

        [TestCategory(Cat), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainTestRegression()
        {
            RunMTAThread(() =>
            {
                const string loaderArgs = "loader=text{header+ col=Lab:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9,1} "
                                       + "xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Feat:Cat,Num}";

                // Perform two FastTree trainings, one with and one without negated labels.
                // The negated labels training should be the same (more or less, up to the
                // influence of any numerical precision issues).
                string trainData = GetDataPath("adult.tiny.with-schema.txt");
                string testData = GetDataPath("adult.tiny.with-schema.txt");
                OutputPath metricsFile = MetricsPath();
                string extraArgs = string.Format("test={{{0}}} tr=ftr{{t=1 numTrees=5 dt+}} lab=Lab feat=Feat norm=Warn", testData);
                TestCore("traintest", trainData, loaderArgs, extraArgs, metricsFile.Arg("dout"));
                _step++;
                metricsFile = MetricsPath();
                TestCore("traintest", trainData, loaderArgs + " xf=Expr{col=Lab expr={x=>-x}}", extraArgs, metricsFile.Arg("dout"));
            });
            Done();
        }

        [TestCategory(Cat), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainTestMoreGains()
        {
            var dataPath = CreateOutputPath("Traindata.txt");
            File.WriteAllLines(dataPath.Path, new string[] {
                "0,0,1,2", "3,0,4,5", "4,0,5,6", "0,1,1,2", "3,1,4,5", "5,1,5,6" });
            const string customGains = "0,3,7,15,35,63";

            RunMTAThread(() =>
            {
                const string loaderArgs = "loader=text{col=Label:0 col=GroupId:U4[0-*]:1 col=Features:~ sep=comma}";
                string extraArgs = string.Format("test={{{0}}} tr=ftrank{{mil=1 nl=2 iter=5 gains={1} dt+}} eval=rank{{gains={1}}}", dataPath.Path, customGains);
                OutputPath metricsFile = MetricsPath();
                TestCore("traintest", dataPath.Path, loaderArgs, extraArgs, metricsFile.Arg("dout"));
            });
            Done();
        }

        [TestCategory(Cat), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTest()
        {
            RunMTAThread(() =>
            {
                // First run a training.
                string trainData = GetDataPath("adult.tiny.with-schema.txt");
                OutputPath trainModel = ModelPath();
                TestCore("train", trainData,
                    "loader=text{header+ col=Lab:0 col=Cat:TX:1-8 col=Num:9-14} xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Feat:Cat,Num}",
                    "tr=ft{t=1} lab=Lab feat=Feat");

                // Then run the testing.
                _step++;
                string testData = GetDataPath(@"..\UCI", "adult.test");
                TestInCore("test", testData, trainModel, "lab=Lab feat=Feat");
            });
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandDataPerformance()
        {
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            // These should all have the same stdout.
            TestReloadedCore("dataperf", pathData, "loader=text{header+}", "col=Label col=Features", null);
            TestReloadedCore("dataperf", pathData, "loader=text{header+}", "col=*", null);
            TestReloadedCore("dataperf", pathData, "loader=text{header+}", "col=0 col=Features", null);
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandScoreComposable()
        {
            // First run a training.
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=text{header+ col=Label:0 col=F:1-9} xf[norm]=ZScore{col=F}", "feat=F tr=ap{shuf-}");

            // Then, run the score.
            // Note that the loader doesn't include the label column any more.
            // We're not baselining this output, it's created to compare against the below.
            _step++;
            OutputPath scorePath = CreateOutputPath("scored.tsv");
            OutputPath scoreModel = ModelPath();
            string loadArgs = string.Format("loader=text{{header+ col=F:Num:1-9}} xf=Load{{tag=norm {0}}}", trainModel.ArgStr("in"));
            const string extraScore = "all=+ saver=Text feat=F";
            TestInOutCore("score", pathData, trainModel, loadArgs + " " + extraScore, scorePath.Arg("dout"));
            TestPipeFromModel(pathData, scoreModel);

            // Another version of scoring, with loadTransforms=true and custom loader.
            // The result should be the same as above.
            _step++;
            OutputPath scorePath2 = CreateOutputPath("scored2.tsv");
            scoreModel = ModelPath();
            loadArgs = "loader=text{header+ col=F:Num:1-9}";
            const string extraScore2 = "all=+ loadTrans+ saver=Text feat=F";
            TestInOutCore("score", pathData, trainModel, loadArgs + " " + extraScore2, scorePath2.ArgOnly("dout"));
            TestPipeFromModel(pathData, scoreModel);
            // Check that the 2nd output matches the 1st output.
            CheckEqualityFromPathsCore(TestName, scorePath.Path, scorePath2.Path);

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandScoreKeyLabelsBinaryClassification()
        {
            // First run a training.
            string pathData = GetDataPath(@"..\UCI\iris", "iris.data");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=Text{sep=comma col=Features:0-3 col=Label:TX:4}" +
                " xf=Expr{col=Label expr={x:x==\"Iris-versicolor\"?\"Iris-virginica\":x}} xf=Term{col=Label}", "tr=lr{t-}");

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.txt");
            OutputPath scoreModel = ModelPath();
            TestInOutCore("score", pathData, trainModel, "all=+ pxf=KeyToValue{col=PredictedLabelText:PredictedLabel}", scorePath.ArgOnly("dout"));
            TestPipeFromModel(pathData, scoreModel);
            scorePath.CheckEqualityNormalized();

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandDefaultLearners()
        {
            string pathData = GetDataPath(TestDatasets.breastCancer.trainFilename);
            TestCore("train", pathData, "", "seed=1 norm=Warn");

            _step++;
            string extraTrainTest = string.Format("seed=2 norm=Warn test={{{0}}}", pathData);
            TestCore("traintest", pathData, "", extraTrainTest);

            _step++;
            TestCore("cv", pathData, null, "seed=3 norm=Warn threads-");
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluate()
        {
            // First run a training.
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=text{header+ col=L:0 col=F:1-9}", "lab=L feat=F tr=lr{t-}");

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            TestInOutCore("score", pathData, trainModel, "all=+ feat=F", scorePath.ArgOnly("dout"));
            TestPipeFromModel(pathData, scoreModel);

            // Duplicate score columns
            _step += 100;
            OutputPath scorePath2 = CreateOutputPath("data2.idv");
            TestCore("savedata", scorePath.Path, "loader=binary xf=Copy{col=PredictedLabel1:PredictedLabel col=Score1:Score}",
                "saver=binary", scorePath2.ArgOnly("dout"));
            _step -= 100;

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath2.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores.
            _step++;
            var outputFile1 = StdoutPath();
            TestCore("evaluate", scorePath2.Path, null, "lab=L");

            // Now, evaluate the text saved scores.
            _step++;
            var outputFile2 = StdoutPath();
            const string evalLoaderArgs = "loader=Text{header+ col=L:Num:0 col=PredictedLabel:Num:10 col=Score:Num:11 col=Probability:Num:12}";
            const string evalExtraArgs = "evaluator=Binary{score=Score prob=Probability} lab=L";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs);

            // Check that the evaluations produced the same result.
            CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);

            // Score with a probability threshold
            _step++;
            OutputPath scorePathTxtProbThresh = CreateOutputPath("prob_thresh_data.txt");
            const string extraScoreProbThresh = "all=+ feat=F scorer=Binary{threshold=0.9 tcol=Probability}";
            TestInCore("score", pathData, trainModel, extraScoreProbThresh, scorePathTxtProbThresh.Arg("dout"));

            // Score with a raw score threshold
            _step++;
            OutputPath scorePathTxtRawThresh = CreateOutputPath("raw_thresh_data.txt");
            const string extraScoreRawThresh = "all=+ feat=F scorer=Binary{threshold=2}";
            TestInCore("score", pathData, trainModel, extraScoreRawThresh, scorePathTxtRawThresh.Arg("dout"));

            // Evaluate with a probability threshold
            _step++;
            TestCore("evaluate", scorePath.Path, null, "evaluator=Binary{threshold=0.9 useRawScore-} lab=L");

            // Evaluate with a raw score threshold
            _step++;
            TestCore("evaluate", scorePath.Path, null, "evaluator=Binary{threshold=2} lab=L");

            // Score using custom output column names
            _step++;
            OutputPath scoreCustomCols = CreateOutputPath("custom_output_columns_data.txt");
            const string extraScoreCustomCols = "all=+ feat=F scorer=Binary{ex=1}";
            TestInCore("score", pathData, trainModel, extraScoreCustomCols, scoreCustomCols.Arg("dout"));

            // Evaluate output from the previous step.
            _step++;
            TestCore("evaluate", scoreCustomCols.Path, null, "evaluator=Binary{score=Score1 prob=Probability1} lab=L");

            Done();
        }

        [TestCategory(Cat), TestCategory("Multiclass")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluateMulticlass()
        {
            // First run a training.
            string pathData = GetDataPath("iris-label-name.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=TextLoader{header+ col=Label:TX:0 col=Features:1-4} xf=Term{col=Label}",
                "lab=Label feat=Features seed=42 tr=MulticlassNeuralNetwork{output=3 accel=sse lr=0.1 iter=70} norm=Warn");

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", pathData, trainModel, extraScore);
            TestPipeFromModel(pathData, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores.
            _step++;
            var outputFile1 = StdoutPath();
            TestCore("evaluate", scorePath.Path, "", "");

            // Now, evaluate the text saved scores.
            _step++;
            var outputFile2 = StdoutPath();
            var metricsPath = MetricsPath();
            const string evalLoaderArgs = "loader=Text{header+ col=Label:TX:0 col=PredictedLabel:5 col=Score:6-8} xf=Term{col=Label} evaluator=Multiclass{score=Score}";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, null, metricsPath.Arg("dout"));

            // Check that the evaluations produced the same result.
            // REVIEW: The following used to pass, until the evaluator started putting the class names in.
            // The trouble is, that the text loader from the output *score* is just a number at the point it is
            // output, since by that time it is key, and the text does not preserve the metadata of columns.
            //CheckEqualityFromPathsCore(TestContext.TestName, outputFile1.Path, outputFile2.Path);

            _step++;
            const string evalLoaderArgs2 = "loader=Text{header+ col=Label:0 col=PredictedLabel:5 col=Score:6-8} evaluator=Multiclass{score=Score opcs+}";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs2, null, metricsPath.Arg("dout"));

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluateUncalibratedBinary()
        {
            // First run a training.
            string pathData = GetDataPath(TestDatasets.breastCancer.trainFilename);
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=TextLoader xf[norm]=MinMax{col=Features}", "tr=ap{shuf=-} cali={}");

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", pathData, trainModel, extraScore);
            TestPipeFromModel(pathData, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores.
            _step++;
            var outputFile1 = StdoutPath();
            OutputPath metricPath1 = MetricsPath();
            TestCore("evaluate", scorePath.Path, "", "", metricPath1.Arg("dout"));

            // Now, evaluate the text saved scores.
            _step++;
            var outputFile2 = StdoutPath();
            const string evalLoaderArgs = "loader=Text{header+ col=Label:0 col=Score:11}";
            OutputPath metricPath2 = MetricsPath();
            const string evalExtraArgs = "evaluator=Bin{score=Score}";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs, metricPath2.Arg("dout"));

            // Check that the evaluations produced the same result.
            CheckEqualityFromPathsCore(TestName, metricPath1.Path, metricPath2.Path);
            CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);
            Done();
        }

        [TestCategory(Cat), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluateRegression()
        {
            RunMTAThread(() =>
            {
                // First run a training.
                string pathData = GetDataPath(@"..\Housing (regression)", "housing.txt");
                OutputPath trainModel = ModelPath();
                TestCore("train", pathData, "loader=text", "lab=Label feat=Features tr=FastTreeRegression{dt+}");

                // Then, run the score.
                _step++;
                OutputPath scorePath = CreateOutputPath("data.idv");
                OutputPath scoreModel = ModelPath();
                string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
                TestInOutCore("score", pathData, trainModel, extraScore);
                TestPipeFromModel(pathData, scoreModel);

                // Transform the score output to txt for baseline
                _step++;
                OutputPath scorePathTxt = CreateOutputPath("data.txt");
                TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

                // First, evaluate the binary saved scores.
                _step++;
                var outputFile1 = StdoutPath();
                TestCore("evaluate", scorePath.Path, "", "");

                // Now, evaluate the text saved scores.
                _step++;
                var outputFile2 = StdoutPath();
                const string evalLoaderArgs = "loader=Text{header+ col=Label:Num:0 col=Score:Num:14}";
                const string evalExtraArgs = "evaluator=Regression{score=Score}";
                TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs);

                // Check that the evaluations produced the same result.
                CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);
            });
            Done();
        }

        [Fact(Skip = "Need CoreTLC specific baseline update")]
        [TestCategory("SDCAR")]
        public void CommandTrainScoreFCCSdcaR()
        {
            // First run a training.
            string pathData = GetDataPath(@"..\Housing (regression)", "housing.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=text", "lab=Label feat=Features tr=Sdcar{l2=0.01 l1=0 iter=10 checkFreq=-1 nt=1} seed=1");

            // Get features contributions.
            _step++;
            OutputPath fccScorerPath = CreateOutputPath("score-fcc.txt");
            TestInOutCore("score", pathData, trainModel, "scorer=fcc outputColumn=FeatureContributions", fccScorerPath.Arg("dout"));

            _step++;
            fccScorerPath = CreateOutputPath("score-fcc_top3.txt");
            TestInOutCore("score", pathData, trainModel, "scorer=fcc{top=3 bottom=3} outputColumn=FeatureContributions", fccScorerPath.Arg("dout"));

            _step++;
            fccScorerPath = CreateOutputPath("score-fcc_top3_noNorm.txt");
            TestInOutCore("score", pathData, trainModel, "scorer=fcc{top=3 bottom=3 norm-}", fccScorerPath.Arg("dout"));
            Done();
        }

        [Fact]
        public void CommandTrainTestFCCAdult()
        {
            string trainData = GetDataPath("adult.tiny.with-schema.txt");
            string testData = GetDataPath("adult.tiny.with-schema.txt");

            const string loaderArgs = "loader=text{header+ col=Lab:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9} "
              + "xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Feat:Cat,Num}";

            OutputPath metricsFile = CreateOutputPath("metrics-fcc.txt");

            string extraArgs = string.Format("test={{{0}}} tr=AP{{shuf-}} scorer=fcc{{top=3 bottom=3}} xf=Take{{c=5}} lab=Lab feat=Feat norm=Warn", testData);
            TestCore("traintest", trainData, loaderArgs, extraArgs, digitsOfPrecision: 6, metricsFile.Arg("dout"));

            // Check stringify option.
            _step++;
            metricsFile = CreateOutputPath("metrics-fcc-str.txt");
            extraArgs = string.Format("test={{{0}}} tr=AP{{shuf-}} scorer=fcc{{top=3 bottom=3 str+}} xf=Take{{c=5}} lab=Lab feat=Feat norm=Warn", testData);
            TestCore("traintest", trainData, loaderArgs, extraArgs, digitsOfPrecision: 6, metricsFile.Arg("dout"));
            Done();
        }

        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreFCCText()
        {
            // Train binary classifier on a small subset of 20NG data
            const string textTransform =
                "xf=TextTransform{{col={0} remover=PredefinedStopWordsRemover punc=- num=- charExtractor={{}} wordExtractor=NgramExtractor}} ";
            string loaderArgs = "loader = TextLoader{col=ID:R4:0 col=Label:TX:1 col=Title:TX:2 col=Body:TX:3 header=+} "
              + "xf=SkipFilter{c=700} xf=TakeFilter{c=400} xf=Term{col=Label max=2} "
              + string.Format(textTransform, "Title")
              + string.Format(textTransform, "Body")
              + "xf=Concat{col=Features:Body,Title}";

            string pathData = GetDataPath(@"..\V3\Data\20NewsGroups", "all-data_small.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, loaderArgs, "tr=AP{shuf-} seed=1");

            // Get features contributions.
            _step++;
            OutputPath fccScorerPath = CreateOutputPath("score-fcc.txt");
            TestInOutCore("score", pathData, trainModel, "scorer=fcc{top=3 bottom=3 str+} xf=Take{c=10} loadTrans+", fccScorerPath.Arg("dout"));

            Done();
        }

        [TestCategory(Cat)]
        [TestCategory("SDCA")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluateSdcaRegression()
        {
            // First run a training.
            string pathData = GetDataPath(@"..\Housing (regression)", "housing.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathData, "loader=text", "lab=Label feat=Features tr=Sdcar{l2=0.01 l1=0 iter=10 checkFreq=-1 nt=1} seed=1");

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", pathData, trainModel, extraScore);
            TestPipeFromModel(pathData, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores.
            _step++;
            var outputFile1 = StdoutPath();
            TestCore("evaluate", scorePath.Path, "", "");

            // Now, evaluate the text saved scores.
            _step++;
            var outputFile2 = StdoutPath();
            const string evalLoaderArgs = "loader=Text{header+ col=Label:Num:0 col=Score:Num:14}";
            const string evalExtraArgs = "evaluator=Regression{score=Score}";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs);

            // Check that the evaluations produced the same result.
            CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);
            Done();
        }

        [TestCategory(Cat), TestCategory("FastForest")]
        [Fact(Skip = "Temporarily skipping while Intel/AMD difference is resolved. Tracked in issue #5845")]
        public void CommandTrainScoreEvaluateQuantileRegression()
        {
            RunMTAThread(() =>
            {
                // First run a training.
                string pathData = GetDataPath("housing.txt");
                OutputPath trainModel = ModelPath();
                TestCore("train", pathData, "loader=Text{header+}", "lab=Label feat=Features tr=FastForestRegression{dt+}");

                // Then, run the score.
                _step++;
                OutputPath scorePath = CreateOutputPath("data.idv");
                OutputPath scoreModel = ModelPath();
                string extraScore = string.Format("all=+ feat=Features scorer=QuantileRegression {0}", scorePath.ArgStr("dout"));
                TestInOutCore("score", pathData, trainModel, extraScore);
                TestPipeFromModel(pathData, scoreModel);

                // Transform the score output to txt for baseline
                _step++;
                OutputPath scorePathTxt = CreateOutputPath("data.txt");
                TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

                // First, evaluate the binary saved scores.
                _step++;
                var outputFile1 = StdoutPath();
                var metricsFile1 = MetricsPath();
                TestCore("evaluate", scorePath.Path, "", "", metricsFile1.Arg("dout"));

                // Now, evaluate the text saved scores.
                _step++;
                var outputFile2 = StdoutPath();
                const string evalLoaderArgs = "loader=Text{header+ col=Label:Num:0 col=Score:Num:14-18}";
                const string evalExtraArgs = "evaluator=QuantileRegression{score=Score}";
                TestReloadedCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs, null);

                // Check that the evaluations produced the same result.
                CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);
            });
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandSaveData()
        {
            // Default to breast-cancer.txt.
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            OutputPath dataPath = CreateOutputPath("data.txt");
            TestReloadedCore("savedata", pathData, "loader=text{header+}", null, null, dataPath.Arg("dout"));
            Done();
        }

        [TestCategory(Cat), TestCategory("CountTargetEncoding")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandDraculaInfer()
        {
            string pathData = GetDataPath(@"..", "breast-cancer-withheader.txt");
            var inferArgs = "b=Simple{featureCount=4 col=Features lab=Label}";
            TestReloadedCore("draculaInfer", pathData, "loader=Text{header+ col=Features:TX:1-3 col=Label:0}", inferArgs, null);
            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandUnsupervisedTrain()
        {
            // First run a training.
            TestDataset dataset = TestDatasets.azureCounterUnlabeled;
            var pathTrain = GetDataPath(dataset.trainFilename);
            var pathTest = GetDataPath(dataset.testFilename);
            var args = "tr=PCAAnom{seed=1} norm=Warn";
            OutputPath trainModel = ModelPath();
            TestCore("train", pathTrain, "loader=Text{sep=space col=Features:1-*}", args);

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            TestInOutCore("score", pathTest, trainModel, "all=+ feat=Features", scorePath.ArgOnly("dout"));
            TestPipeFromModel(pathTest, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            Done();
        }

        [TestCategory(Cat)]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainScoreEvaluateAnomalyDetection()
        {
            // First run a training.
            TestDataset dataset = TestDatasets.mnistOneClass;
            var pathTrain = GetDataPath(dataset.trainFilename);
            var pathTest = GetDataPath(dataset.testFilename);
            var args = "tr=PCAAnom{seed=1} norm=Warn";
            OutputPath trainModel = ModelPath();
            const string trainLoaderArgs = "loader=Text{col=Label:0 col=Features:1-*}";
            TestCore("train", pathTrain, trainLoaderArgs, args);

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", pathTest, trainModel, extraScore);
            TestPipeFromModel(pathTest, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores.
            _step++;
            OutputPath outputFile1 = StdoutPath();
            TestCore("evaluate", scorePath.Path, "", "");

            // Now, evaluate the text saved scores.
            _step++;
            OutputPath outputFile2 = StdoutPath();
            OutputPath metricsPath = MetricsPath();
            const string evalLoaderArgs = "loader=Text{header+ col=Label:0 col=Score:785}";
            const string evalExtraArgs = "evaluator=Anomaly{score=Score}";
            TestCore("evaluate", scorePathTxt.Path, evalLoaderArgs, evalExtraArgs, metricsPath.Arg("dout"));

            // Check that the evaluations produced the same result.
            CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);

            // Run a train-test. The output should be the same as the train output, plus the test output.
            _step++;
            string trainTestExtra = string.Format("{0} test={{{1}}}", args, pathTest);
            TestCore(Params, "traintest", pathTrain, trainLoaderArgs, trainTestExtra);

            Done();
        }

        [TestCategory(Cat), TestCategory("Ranking"), TestCategory("FastTree")]
        [Fact]
        public void CommandTrainRanking()
        {
            // First run a training.
            var dataPath = GetDataPath(TestDatasets.adultRanking.trainFilename);
            OutputPath trainModel = ModelPath();
            const string extraArgs = "xf=Term{col=Label col=GroupId:Workclass} xf=Copy{col=Features:NumericFeatures} tr=frrank{mil=30 lr=0.1 iter=10 dt+}";
            RunMTAThread(() => TestCore("train", dataPath, TestDatasets.adultRanking.loaderSettings, extraArgs));

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", dataPath, trainModel, extraScore);
            TestPipeFromModel(dataPath, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // First, evaluate the binary saved scores. This exercises the saved score type in the metadata,
            // that is, we are deliberately not specifying the evaluator type.
            _step++;
            OutputPath outputFile1 = StdoutPath();
            OutputPath summaryFile1 = CreateOutputPath("summary1.txt");
            OutputPath metricsFile1 = MetricsPath();
            TestCore("evaluate", scorePath.Path, "", null, summaryFile1.Arg("sf"), metricsFile1.Arg("dout"));

            // Now, evaluate the text saved scores. Also exercise the gsf evaluator option while we're at it.
            _step++;
            OutputPath outputFile2 = StdoutPath();
            OutputPath summaryFile2 = CreateOutputPath("summary2.txt");
            OutputPath metricsFile2 = MetricsPath();
            OutputPath groupSummaryFile2 = CreateOutputPath("gsummary2.txt");
            TestCore("evaluate", scorePathTxt.Path, "loader=Text", "eval=Ranking{score=Score}",
                groupSummaryFile2.Arg("eval", "gsf"), summaryFile2.ArgOnly("sf"), metricsFile2.ArgOnly("dout"));
            // Check that the evaluations produced the same result.
            CheckEqualityFromPathsCore(TestName, outputFile1.Path, outputFile2.Path);
            CheckEqualityFromPathsCore(TestName, summaryFile1.Path, summaryFile2.Path);
            CheckEqualityFromPathsCore(TestName, metricsFile1.Path, metricsFile2.Path);

            //// Run a train-test. The output should be the same as the train output, plus the test output.
            _step++;
            string trainTestExtra = string.Format("{0} test={1}", extraArgs, dataPath);
            RunMTAThread(() => TestCore("traintest", dataPath, TestDatasets.adultRanking.loaderSettings, trainTestExtra));

            Done();
        }

        [TestCategory(Cat), TestCategory("EarlyStopping"), TestCategory("FastTree")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainTestEarlyStopping()
        {
            const string loaderCmdline = @"loader=Text{header+ col=Label:1 col=Features:2-*}";
            string pathTrain = GetDataPath("MSM-sparse-sample-train.txt");
            string pathValid = GetDataPath("MSM-sparse-sample-test.txt");
            // The extra xf=Concat transform is used to make sure that the transforms are applied to the validation set as well.
            string extraArgs = string.Format("tr=FastRank{{nl=5 mil=5 lr=0.25 iter=1000 pruning=+ esr=PQ esmt=0 dt+}} test={{{0}}} valid={{{0}}}", pathValid);
            RunMTAThread(() => TestCore("traintest", pathTrain, loaderCmdline, extraArgs));

            Done();
        }

        [TestCategory(Cat), TestCategory("EarlyStopping")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainEarlyStopping()
        {
            TestDataset dataset = TestDatasets.mnistTiny28;
            var pathTrain = GetDataPath(dataset.trainFilename);
            var pathValid = GetDataPath(dataset.testFilename);
            // The extra xf=Concat transform is used to make sure that the transforms are applied to the validation set as well.
            var extraArgs = string.Format("tr=mcnn{{output=10 lr=0.1 iter=1000 esr=PQ esmt=0 accel=sse}} seed=13 valid={{{0}}}", pathValid);
            TestCore("Train", pathTrain, "loader=Text{col=Label:0 col=Fx:1-*} xf=Concat{col=Features:Fx}", extraArgs);

            Done();
        }

        [TestCategory(Cat), TestCategory("Clustering")]
        [Fact(Skip = "Need CoreTLC specific baseline update")]
        public void CommandTrainClustering()
        {
            // First run a training.
            const string loaderCmdline = "loader=text{header+ col=Label:0 col=Cat:TX:1-8 col=Num:9-14} xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Features:Cat,Num}";
            const string loaderCmdlineNoLabel = "loader=text{header+ col=Cat:TX:1-8 col=Num:9-14} xf=Cat{col=Cat} xf=MinMax{col=Num} xf=Concat{col=Features:Cat,Num}";
            const string trainArgs = "seed=13 tr=KM{init=random nt=1}";
            string pathTrain = GetDataPath("adult.tiny.with-schema.txt");
            OutputPath trainModel = ModelPath();
            TestCore("train", pathTrain, loaderCmdline, trainArgs);

            // Then, run the score.
            _step++;
            OutputPath scorePath = CreateOutputPath("data.idv");
            OutputPath scoreModel = ModelPath();
            string extraScore = string.Format("all=+ feat=Features {0}", scorePath.ArgStr("dout"));
            TestInOutCore("score", pathTrain, trainModel, extraScore);
            TestPipeFromModel(pathTrain, scoreModel);

            // Transform the score output to txt for baseline
            _step++;
            OutputPath scorePathTxt = CreateOutputPath("data.txt");
            TestCore("savedata", scorePath.Path, "loader=binary", "saver=text", scorePathTxt.Arg("dout"));

            // Run a train-test. The output should be the same as the train output, plus the test output.
            _step++;
            OutputPath metricsFile = MetricsPath();
            string trainTestExtra = string.Format("{0} test={{{1}}} eval=Clustering{{dbi+}}", trainArgs, pathTrain);
            TestCore("traintest", pathTrain, loaderCmdline, trainTestExtra, metricsFile.Arg("dout"));

            // Now run the same train-test with no label, make sure the output is produced correctly.
            _step++;
            metricsFile = MetricsPath();
            trainTestExtra = string.Format("{0} test={{{1}}} eval=Clustering{{dbi+}}", trainArgs, pathTrain);
            TestCore("traintest", pathTrain, loaderCmdlineNoLabel, trainTestExtra, metricsFile.Arg("dout"));

            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [FieldAwareFactorizationMachineFact]
        public void CommandTrainingBinaryFactorizationMachineWithInitialization()
        {
            const string loaderArgs = "loader=text{col=Label:0 col=Features:1-*}";
            const string extraArgs = "xf=minmax{col=Features} tr=ffm{d=7 shuf- iters=3 norm-}";
            string data = GetDataPath(TestDatasets.breastCancer.trainFilename);
            OutputPath model = ModelPath();

            TestCore("traintest", data, loaderArgs, extraArgs + " test=" + data, 6);

            _step++;
            TestInOutCore("traintest", data, model, extraArgs + " " + loaderArgs + " " + "cont+" + " " + "test=" + data, 6);
            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [FieldAwareFactorizationMachineFact]
        public void CommandTrainingBinaryFieldAwareFactorizationMachineWithInitialization()
        {
            const string loaderArgs = "loader=text{col=Label:0 col=FieldA:1-2 col=FieldB:3-4 col=FieldC:5-6 col=FieldD:7-9}";
            const string extraArgs = "tr=ffm{d=7 shuf- iters=3} col[Feature]=FieldA col[Feature]=FieldB col[Feature]=FieldC col[Feature]=FieldD";
            string data = GetDataPath(TestDatasets.breastCancer.trainFilename);
            OutputPath model = ModelPath();

            TestCore("traintest", data, loaderArgs, extraArgs + " test=" + data, digitsOfPrecision: 5);

            _step++;
            TestInOutCore("traintest", data, model, extraArgs + " " + loaderArgs + " " + "cont+" + " " + "test=" + data, 6);
            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [Fact(Skip = "Missing Dataset.")]
        public void CommandTrainingBinaryFactorizationMachineWithValidation()
        {
            const string loaderArgs = "loader=Text{header+ col=Label:1 col=Features:2-*}";
            string trainData = GetDataPath("MSM-sparse-sample-train.txt");
            string validData = GetDataPath("MSM-sparse-sample-test.txt");
            const string extraArgs = "xf=minmax{col=Features} tr=ffm{d=5 shuf- iters=3 norm-} norm=No";
            OutputPath model = ModelPath();
            string args = $"{loaderArgs} data={trainData} valid={validData} test={validData} {extraArgs} out={model}";
            OutputPath outputPath = StdoutPath();
            using (var newWriter = OpenWriter(outputPath.Path))
            using (_env.RedirectChannelOutput(newWriter, newWriter))
            {
                _env.ResetProgressChannel();
                int res = MainForTest(_env, newWriter, string.Format("{0} {1}", "traintest", args), true);
                Assert.True(res == 0);
            }

            // see https://github.com/dotnet/machinelearning/issues/404
            // in Linux, the clang sqrt() results vary highly from the ones in mac and Windows.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Assert.True(outputPath.CheckEqualityNormalized(digitsOfPrecision: 4));
            else
                Assert.True(outputPath.CheckEqualityNormalized());

            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [Fact(Skip = "Missing Dataset.")]
        public void CommandTrainingBinaryFieldAwareFactorizationMachineWithValidation()
        {
            const string loaderArgs = "loader=Text{header+ col=Label:1 col=FieldA:2-20 col=FieldB:21-40 col=FieldC:41-60 col=FieldD:61-*}";
            string trainData = GetDataPath("MSM-sparse-sample-train.txt");
            string validData = GetDataPath("MSM-sparse-sample-test.txt");
            const string extraArgs = "tr=ffm{d=5 shuf- iters=3} norm=No xf=minmax{col=FieldA col=FieldB col=FieldC col=FieldD} col[Feature]=FieldA col[Feature]=FieldB col[Feature]=FieldC col[Feature]=FieldD";
            OutputPath model = ModelPath();
            string args = $"{loaderArgs} data={trainData} valid={validData} test={validData} {extraArgs} out={model}";
            OutputPath outputPath = StdoutPath();
            using (var newWriter = OpenWriter(outputPath.Path))
            using (_env.RedirectChannelOutput(newWriter, newWriter))
            {
                _env.ResetProgressChannel();
                int res = MainForTest(_env, newWriter, string.Format("{0} {1}", "traintest", args), true);
                Assert.Equal(0, res);
            }

            Assert.True(outputPath.CheckEqualityNormalized());

            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [Fact(Skip = "Temporarily skipping while Intel/AMD difference is resolved. Tracked in issue #5845")]
        public void CommandTrainingBinaryFactorizationMachineWithValidationAndInitialization()
        {
            const string loaderArgs = "loader=text{col=Label:0 col=Features:1-*}";
            const string extraArgs = "xf=minmax{col=Features} tr=ffm{d=5 shuf- iters=2 norm-}";
            string data = GetDataPath(TestDatasets.breastCancer.trainFilename);
            OutputPath model = ModelPath();

            TestCore("traintest", data, loaderArgs, extraArgs + " test=" + data, 6);

            _step++;
            OutputPath outputPath = StdoutPath();
            string args = $"data={data} test={data} valid={data} in={model.Path} cont+" + " " + loaderArgs + " " + extraArgs;
            using (var newWriter = OpenWriter(outputPath.Path))
            using (_env.RedirectChannelOutput(newWriter, newWriter))
            {
                _env.ResetProgressChannel();
                int res = MainForTest(_env, newWriter, string.Format("{0} {1}", "traintest", args), true);
                Assert.True(res == 0);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Assert.True(outputPath.CheckEqualityNormalized(digitsOfPrecision: 4));
            else
                Assert.True(outputPath.CheckEqualityNormalized());

            Done();
        }

        [TestCategory(Cat), TestCategory("FieldAwareFactorizationMachine"), TestCategory("Continued Training")]
        [Fact(Skip = "Temporarily skipping while Intel/AMD difference is resolved. Tracked in issue #5845")]
        public void CommandTrainingBinaryFieldAwareFactorizationMachineWithValidationAndInitialization()
        {
            const string loaderArgs = "loader=text{col=Label:0 col=FieldA:1-2 col=FieldB:3-4 col=FieldC:5-6 col=FieldD:7-9}";
            const string extraArgs = "tr=ffm{d=5 shuf- iters=2} col[Feature]=FieldA col[Feature]=FieldB col[Feature]=FieldC col[Feature]=FieldD";
            string data = GetDataPath(TestDatasets.breastCancer.trainFilename);
            OutputPath model = ModelPath();

            TestCore("traintest", data, loaderArgs, extraArgs + " test=" + data, 6);

            _step++;
            OutputPath outputPath = StdoutPath();
            string args = $"data={data} test={data} valid={data} in={model.Path} cont+" + " " + loaderArgs + " " + extraArgs;
            using (var newWriter = OpenWriter(outputPath.Path))
            using (_env.RedirectChannelOutput(newWriter, newWriter))
            {
                _env.ResetProgressChannel();
                int res = MainForTest(_env, newWriter, string.Format("{0} {1}", "traintest", args), true);
                Assert.True(res == 0);
            }

            Assert.True(outputPath.CheckEqualityNormalized());

            Done();
        }

        [Fact]
        public void Datatypes()
        {
            string idvPath = GetDataPath("datatypes.idv");
            OutputPath intermediateData = CreateOutputPath("intermediateDatatypes.idv");
            OutputPath textOutputPath = CreateOutputPath("datatypes.txt");
            TestCore("savedata", idvPath, "loader=binary", "saver=text", textOutputPath.Arg("dout"));
            _step++;
            TestCore("savedata", idvPath, "loader=binary", "saver=binary", intermediateData.ArgOnly("dout"));
            _step++;
            TestCore("savedata", intermediateData.Path, "loader=binary", "saver=text", textOutputPath.Arg("dout"));
            Done();
        }

        [TestCategory("DataPipeSerialization")]
        [Fact()]
        public void SavePipeChooseColumnsByIndex()
        {
            string dataPath = GetDataPath("adult.tiny.with-schema.txt");
            const string loaderArgs = "loader=text{header+ col=Label:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9}";

            OutputPath modelPath = ModelPath();
            string extraArgs = "xf=ChooseColumnsByIndex{ind=3 ind=0}";
            TestCore("showdata", dataPath, loaderArgs, extraArgs);

            _step++;

            TestCore("showdata", dataPath, string.Format("in={{{0}}}", modelPath.Path), "");
            Done();
        }

        [TestCategory("DataPipeSerialization")]
        [Fact()]
        public void SavePipeTextLoaderWithMultilines()
        {
            string dataPath = GetDataPath("multiline-escapechar.csv");
            const string loaderArgs = "loader=text{sep=, quote+ multilines+ header+ escapechar=\\ col=id:Num:0 col=description:TX:1 col=animal:TX:2}";

            OutputPath modelPath = ModelPath();
            string extraArgs = null;
            TestCore("showdata", dataPath, loaderArgs, extraArgs);

            _step++;

            TestCore("showdata", dataPath, string.Format("in={{{0}}}", modelPath.Path), "");
            Done();
        }

        [TestCategory("DataPipeSerialization")]
        [Fact()]
        public void SavePipeTextLoaderWithMissingRealsAsNaNs()
        {
            string dataPath = GetDataPath("missing_fields.csv");
            const string loaderArgs = "loader=text{sep=, quote+ multilines+ header+ escapechar=\\ missingrealnan+ " +
                "col=id:Num:0 col=description:TX:1 col=date:DT:4 " +
                "col=sing1:R4:2 col=sing2:R4:3 col=singFt1:R4:2-3 " +
                "col=doubFt:R8:2-3,5-6}";

            OutputPath modelPath = ModelPath();
            string extraArgs = null;
            TestCore("showdata", dataPath, loaderArgs, extraArgs);

            _step++;

            TestCore("showdata", dataPath, string.Format("in={{{0}}}", modelPath.Path), "");
            Done();
        }

        [TestCategory("DataPipeSerialization")]
        [Fact()]
        public void SavePipeChooseColumnsByIndexDrop()
        {
            string dataPath = GetDataPath("adult.tiny.with-schema.txt");
            const string loaderArgs = "loader=text{header+ col=Label:0 col=Cat:TX:1-8 col=Num:9-14 col=Name:TX:9}";

            OutputPath modelPath = ModelPath();

            string extraArgs = "xf=ChooseColumnsByIndex{ind=3 ind=0 drop+}";
            TestCore("showdata", dataPath, loaderArgs, extraArgs);

            _step++;

            TestCore("showdata", dataPath, string.Format("in={{{0}}}", modelPath.Path), "");
            Done();
        }

        [Fact]
        public void CommandShowDataSvmLight()
        {
            // Test with a specified size parameter. The "6" feature should be omitted.
            // Also the blank and completely fully commented lines should be omitted,
            // and the feature 2:3 that appears in the comment should not appear.
            var path = CreateOutputPath("DataA.txt");
            File.WriteAllLines(path.Path, new string[] {
                "1\t1:3\t4:6",
                "  -1 cost:5\t2:4 \t4:7\t6:-1   ",
                "",
                "1\t5:-2 # A comment! 2:3",
                "# What a nice full line comment",
                "1 cost:0.5\t2:3.14159",
            });
            var pathA = path.Path;
            const string chooseXf = " xf=select{keepcol=Label keepcol=Weight keepcol=GroupId keepcol=Comment keepcol=Features}";
            TestReloadedCore("showdata", path.Path, "loader=svm{size=5}" + chooseXf, "", "");

            // Test with autodetermined sizes. The the "6" feature should be included,
            // and the feature vector should have length 6.
            _step++;
            TestCore("showdata", path.Path, "loader=svm" + chooseXf, "");

            // Test with a term mapping, instead of the actual SVM^light format that
            // requires positive integers. ALso check that qid works here.
            _step++;
            var modelPath = ModelPath();
            path = CreateOutputPath("DataB.txt");
            File.WriteAllLines(path.Path, new string[] {
                "1 qid:1 aurora:3.14159 beachwood:123",
                "-1 qid:5 beachwood:345 chagrin:-21",
            });
            TestReloadedCore("showdata", path.Path, "loader=svm{indices=names}" + chooseXf, "", "");

            // We reload the model, but on a new set of data. The "euclid" key should be
            // ignored as it would not have been detected by the term transform.
            _step++;
            path = CreateOutputPath("DataC.txt");
            File.WriteAllLines(path.Path, new string[] {
                "-1 aurora:1 chagrin:2",
                "1 chagrin:3 euclid:4"
            });
            TestInCore("showdata", path.Path, modelPath, "");

            _step++;
            path = CreateOutputPath("DataD.txt");
            File.WriteAllLines(path.Path, new string[] { "1 aurora:2 :3" });
            TestReloadedCore("showdata", path.Path, "loader=svm{indices=names}" + chooseXf, "", "");

            _step++;

            // If we specify the size parameter, and zero-based feature indices, both indices 5 and 6 should
            // not appear.
            TestReloadedCore("showdata", pathA, "loader=svm{size=5 indices=zerobased}" + chooseXf, "", "");

            Done();
        }

        [Fact]
        public void CommandSaveDataSvmLight()
        {
            string pathData = GetDataPath("breast-cancer-withheader.txt");
            OutputPath dataPath = CreateOutputPath("data.txt");
            TestReloadedCore("savedata", pathData, "loader=text{header+}", "saver=svmlight{b+}", null, dataPath.Arg("dout"));
            dataPath = CreateOutputPath("data-0.txt");
            TestReloadedCore("savedata", pathData, "loader=text{header+}", "saver=svmlight{zero+}", null, dataPath.Arg("dout"));
            Done();
        }
    }
}
