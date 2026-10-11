# B25 ABI test

This fixture calls the actual `ExternalB25DecoderRuntime` in a built product worker with a native mock implementing the first five IB25Decoder virtual members. It checks Initialize(4), Decode input/output, Flush, and Release under x86 and x64. It does not decrypt TS or access a card.

Build the product for the same platform, then build this project with `dotnet build -c Release -p:Platform=x86` (or x64). In the corresponding Visual Studio developer command prompt, compile MockB25.cpp as a DLL using MockB25.def and place the resulting B25Decoder.dll only in this test project's output directory. Run the test executable with the full path to the product TvAIrEpgRec.dll as its argument.

**Never copy the mock B25Decoder.dll into a real TvAIr/TVTest/EDCB installation.** It only returns test data. Generated test binaries are excluded from the full-source ZIP.
