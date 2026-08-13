# LabAcacia.NPS.Conformance

Small CI-friendly conformance primitives for NPS Node self-certification.

```csharp
var manifest = NpsConformanceManifest.Create(
    profile: NpsConformanceProfiles.NodeL1,
    iutName: "my-node",
    iutVersion: "0.1.0",
    iutNid: "urn:nps:node:example.test:node-1",
    peerName: "nps-dotnet-reference",
    peerVersion: "1.0.0-alpha.18",
    results: myCaseResults);

var validation = NpsConformanceValidator.Validate(manifest);
if (!validation.Valid) throw new InvalidOperationException(validation.Message);
```

The package ships the L1 and current L2 case catalogs, a manifest model, and validation helpers.
