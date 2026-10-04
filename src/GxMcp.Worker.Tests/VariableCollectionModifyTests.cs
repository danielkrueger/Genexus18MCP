using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #405: a variable of SDT, Business Component or built-in type could not be
    /// marked as a collection. Two separate refusals combined to produce it.
    ///
    /// <para>
    /// <b>The retype gate.</b> <c>modify collection=true</c> demanded a
    /// <c>newTypeName</c>, because <c>collection</c> was grouped with the arguments that
    /// reshape a type.
    ///
    /// <para>
    /// <b>The type resolution.</b> supplying one did not help.
    /// <c>VariableTypeResolver</c> classifies <em>every</em> bare non-primitive name as
    /// <c>DomainReference</c> - SDT, BC and every built-in included - and the modify path
    /// resolved only a Domain, answering <c>Domain 'SDTDashRec' was not found</c> before
    /// the binding code that handles those types could ever run.
    /// </para>
    ///
    /// <para>
    /// <c>add</c> already resolved all of them correctly, so the fix is to give modify the
    /// same ordered resolution rather than to teach the resolver something new. These tests
    /// are source- and shape-level: the binding calls need a live SDK object, and the
    /// existing fake-model fixtures cover the binder itself.
    /// </para>
    /// </summary>
    public class VariableCollectionModifyTests
    {
        [Fact]
        public void A_Collection_Only_Modify_No_Longer_Demands_A_New_Type_Name()
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            // The gate used to fold collection into the "reshapes the type" group, which is
            // what made a collection-only modify unreachable without a retype.
            Assert.DoesNotContain(
                "|| length.HasValue || decimals.HasValue || collection.HasValue",
                source);
            Assert.Contains("bool typeReshapingArgs", source);
            Assert.Contains("bool flagOnly = !typeReshapingArgs && collection.HasValue;", source);
        }

        [Fact]
        public void A_Retype_Of_A_Non_Domain_Type_Is_No_Longer_Refused_As_A_Missing_Domain()
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            // The old branch refused on a Domain miss alone, which is how an SDT name reached
            // the caller as "Domain 'SDTDashRec' was not found".
            Assert.DoesNotContain("$\"Domain '{resolvedTypeForSdk}' was not found.", source);
            Assert.Contains("!CanResolveNonDomainType(varPart.Model, resolvedTypeForSdk)", source);
        }

        [Fact]
        public void The_Non_Domain_Probe_Covers_Every_Kind_Of_Type_That_Used_To_Be_Refused()
        {
            // Ordered the same way the binding path in ModifyVariableInternal resolves, so the
            // probe and the binder cannot disagree about what is bindable.
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");
            int start = source.IndexOf("private static bool CanResolveNonDomainType", StringComparison.Ordinal);
            Assert.True(start > 0, "CanResolveNonDomainType was not found");

            string body = source.Substring(start, Math.Min(1200, source.Length - start));
            int objects = body.IndexOf("ResolveTypeObject", StringComparison.Ordinal);
            int gxType = body.IndexOf("IsGenexusDataType", StringComparison.Ordinal);
            int builtin = body.IndexOf("IsBuiltinUserDefinedType", StringComparison.Ordinal);

            Assert.True(objects > 0, "the SDT/Business-Component lookup is missing");
            Assert.True(gxType > objects, "the built-in GeneXus data type lookup must follow the object lookup");
            Assert.True(builtin > gxType, "the built-in user-defined lookup must come last");
        }

        [Fact]
        public void The_Probe_Is_Side_Effect_Free_So_It_Can_Run_Before_A_Variable_Exists()
        {
            // issue #405: the decision has to be made before there is a variable to bind onto,
            // so the probe cannot be TryBindGenexusDataType, which mutates. It mirrors the same
            // lookup and the same Domain refusal so it can never claim a type the binder rejects.
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Helpers", "VariableInjector.cs");
            int start = source.IndexOf("public static bool IsGenexusDataType", StringComparison.Ordinal);
            Assert.True(start > 0, "IsGenexusDataType was not found");

            string body = source.Substring(start, Math.Min(1200, source.Length - start));
            Assert.Contains("GetTypeByName", body);
            Assert.Contains("GX_DOM_REF", body);
            Assert.DoesNotContain("SetPropertyValue", body);
            Assert.DoesNotContain("v.Type =", body);
        }

        [Fact]
        public void The_Collection_Flag_Is_Verified_On_Read_Back_So_It_Fails_Closed()
        {
            // A reported success that the SDK did not persist is the failure this whole issue
            // is about, in the opposite direction.
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            Assert.Contains("persistedCollection != collection.Value", source);
            Assert.Contains("existing.IsCollection = collection.Value;", source);
        }

        [Fact]
        public void An_Untouched_Flag_Is_Neither_Written_Nor_Verified()
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            Assert.Contains("if (description != null) existing.Description = description;", source);
            Assert.Contains("if (collection.HasValue) existing.IsCollection = collection.Value;", source);
            Assert.Contains("if (description != null\n                        && !string.Equals(persistedDescription", source.Replace("\r\n", "\n"));
        }

        [Fact]
        public void A_Rollback_Restores_Both_Flags_It_Touched()
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            Assert.Contains("if (description != null) existing.Description = previousDescription;", source);
            Assert.Contains("if (collection.HasValue) existing.IsCollection = previousCollection;", source);
        }
    }
}
