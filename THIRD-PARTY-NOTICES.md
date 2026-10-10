# Third-party notices

## `go-db2`

This driver uses the [`go-db2`](https://github.com/go-db2/go-db2) project as its primary DRDA protocol and implementation reference. The notice below is retained for upstream code portions derived or adapted for this driver.

```text
MIT License

Copyright (c) 2026 Nill Rezende (https://github.com/nillvitor)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

This notice records provenance; it is not an endorsement by the `go-db2` authors.

## Protocol references and package scope

The driver uses `go-db2` under the MIT notice above. Apache Derby and IBM protocol documentation were consulted as references; the maintainer confirms no Derby code was copied or adapted. IBM and Derby software are not runtime dependencies.

The NuGet package does not contain IBM provider binaries, IBM CLI libraries, or Derby software. IBM server licensing and deployment entitlements are separate from this project's license.
