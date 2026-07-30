# Financial Advisor v2 local development build

The Financial Advisor v2 brokerage changes consume LEAN contracts that are not yet available in a published package. A clean brokerage checkout is therefore expected to build only after the corresponding LEAN changes merge and packages containing those contracts are published.

For local development before publication, place the following untracked `Directory.Build.targets` in the brokerage repository root beside this file, with the sibling LEAN checkout at `../Lean`. This is the exact override used for the local sibling-source integration build:

```xml
<Project>
  <PropertyGroup>
    <UseLocalLean Condition="'$(UseLocalLean)' == ''">true</UseLocalLean>
  </PropertyGroup>
  <ItemGroup Condition="'$(UseLocalLean)' == 'true' and '$(MSBuildProjectName)' == 'QuantConnect.InteractiveBrokersBrokerage'">
    <PackageReference Remove="QuantConnect.Brokerages" />
    <PackageReference Remove="QuantConnect.Lean.Engine" />
    <ProjectReference Include="$(MSBuildThisFileDirectory)..\Lean\Common\QuantConnect.csproj" />
    <ProjectReference Include="$(MSBuildThisFileDirectory)..\Lean\Brokerages\QuantConnect.Brokerages.csproj" />
    <ProjectReference Include="$(MSBuildThisFileDirectory)..\Lean\Engine\QuantConnect.Lean.Engine.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'QuantConnect.InteractiveBrokersBrokerage.Tests'">
    <Compile Remove="InteractiveBrokersFinancialAdvisorPaperIntegrationTests.cs" />
  </ItemGroup>
</Project>
```

The final `Compile Remove` applies only to the ignored local paper-integration harness. It does not exclude a tracked test. Do not commit the override: the brokerage pull request should continue to use package references and, after LEAN publication, pin a package version containing the new contracts.

The override used for the final integration build has SHA-256
`f391eaeaa83d14cdd6a5984a50f518df2f8b866ce912ce38d1305be1b186befe`.
With the override in place, reproduce that build from the brokerage repository root:

```bash
dotnet build QuantConnect.InteractiveBrokersBrokerage.sln -c Release -m:1 -v:minimal
```

Release sequencing is therefore: merge the LEAN contracts and API first, publish packages
containing them, then update the brokerage package pins and validate a clean brokerage checkout
without this local override.
