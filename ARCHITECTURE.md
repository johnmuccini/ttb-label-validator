# Architecture decisions and project roadmap

This document records how the prototype requirements were interpreted, the decisions made during implementation, the evidence behind those decisions, and the limitations that remain. It distinguishes explicit assignment requirements from engineering targets and prototype-specific policy choices.

## Distilled requirements

### 1. Processing speed

The stakeholder notes say that results must return in approximately five seconds or agents will abandon the tool. Five seconds is therefore the end-to-end usability limit, including document parsing, label extraction, the external image-provider call, validation, and presentation.

The local work should consume only a fraction of that budget. PDF parsing and validation target less than one second per ordinary application. A ten-run local benchmark of the published validator executable, including process startup, averaged 285.2 ms per application (253.9–382.0 ms). This measures the validation stage only. End-to-end performance cannot be claimed until the hosted pipeline is benchmarked, because the external image call is expected to dominate latency. Live Gemini testing also produced intermittent HTTP 503 capacity responses, showing that average latency, tail latency, retries, and provider availability must all be measured.

Batch processing should use controlled concurrency so that one slow application does not block the entire batch, while respecting provider rate limits. The user interface should show progress and return completed results even when individual files fail.

### 2. Batch uploads

The application must accept both one application and a batch. The stakeholder examples describe batches of 200–300 applications. Batch support is therefore a core workflow rather than an optional convenience.

Each file needs an independent result and error state. A malformed document, unreadable label, or provider failure must not discard the rest of a batch.

### 3. Outbound access is a runtime dependency

The current design sends extracted label images to the Gemini API. Outbound HTTPS access to `generativelanguage.googleapis.com` is therefore critical to live operation. The application should run a connectivity/configuration preflight and distinguish network or provider failures from label-compliance findings.

This requirement conflicts with the stakeholder observation that the government network blocks many outbound domains. A deployable version needs an approved egress path, domain allowlisting, or a different image provider available within the permitted environment. Synthetic responses support local development and validation-engine testing, but they do not replace the live image provider in normal operation.

The Gemini API key must remain a server-side secret. It must never be embedded in browser code, committed to Git, or written into generated request artifacts.

### 4. Ease of use

The interface must accommodate users with widely different levels of technical comfort. The primary workflow should be visible without configuration screens or technical terminology:

1. Select or drop one or more application PDFs.
2. Start verification.
3. See progress and a clear result for each file.
4. Open the supporting values, label evidence, and reason for any finding.

Provider errors, invalid PDFs, and inconclusive text should be explained in plain language. The application should preserve enough detail for technical diagnosis without requiring compliance agents to interpret logs or raw JSON.

### 5. Dispositions

The prototype will use five result statuses:

- **Approved:** every automatically evaluated rule passes and no required observation is unreadable, conflicting, or missing.
- **Rejected:** one or more deterministic validation rules fail. The result must include every failure found rather than stopping at the first failure. Government-warning violations, application-to-label mismatches, missing mandatory label fields, and supported ABV-rule violations are all reported as rejections with specific reasons.
- **Manual review:** Gemini cannot reliably extract a value, reports ambiguous or conflicting visible text, or otherwise returns an inconclusive observation that requires a person to inspect the label.
- **Malformed input:** the submitted PDF cannot be parsed, uses an unsupported form or layout, or the validator receives missing, structurally invalid, or internally inconsistent application data. This is an input-processing result rather than a compliance finding.
- **External service unavailable:** the image provider remains unavailable after the configured retries, including network failures, timeouts, throttling, unavailable models, and unusable provider responses.

This disposition policy is a project decision based on the stakeholder notes. It must be encoded in the validation engine rather than the image-extraction prompt. A confirmed violation is rejected; uncertainty in image extraction is sent for manual review.

A provider or network failure is a processing failure, not evidence of noncompliance. The batch result must distinguish those states.

### 6. Government-warning presentation

The stakeholder notes specifically call out exact wording, an all-capital `GOVERNMENT WARNING:` heading, bold heading text, and attempts to bury the warning in smaller type. Gemini should therefore return observations about:

- the exact warning text;
- the heading exactly as printed;
- whether the heading is all capitals;
- whether the heading is visibly bold;
- the warning's apparent size relative to nearby label text, including an `undetermined` result when the image does not support a reliable comparison;
- evidence identifying the image and visible text used for each observation.

The validation engine, not Gemini, will decide how those observations affect disposition. Relative size is a prototype visual heuristic inferred from the stakeholder scenario. It is not a measurement of the physical type-size requirements in 27 CFR part 16.

### 7. Additional test labels

The assignment encourages candidates to create or source additional test labels and notes that AI image-generation tools may be useful. The test corpus is therefore an intentional part of the solution, not only temporary development data.

The existing corpus was generated deterministically rather than with an image-generation model. Label artwork was rendered from external JSON data with Pillow, and the current fillable Form 5100.31 was populated and combined with that artwork using PDF libraries. This choice makes cases reproducible, keeps expected values exact, and allows one property at a time to be varied for validation tests.

The corpus currently includes:

- 30 intended-pass applications: ten wine, ten distilled spirits, and ten malt beverages;
- 30 non-passing applications, including one case with six simultaneous failures to verify that the validator returns all findings;
- warning-text, capitalization, bolding, omission, and readability cases;
- missing and mismatched application or label fields;
- imported-product cases;
- a missing-label case;
- two undersized-warning images used to test transcription reliability;
- whisky below its regulatory minimum ABV and dessert wine above its regulatory maximum ABV;
- five live Gemini responses and fixture-backed synthetic responses for the remaining cases.

All applications and labels are visibly identified as synthetic and not for submission.
Passing labels render the government-warning body at the same font size as ordinary producer and address text. The deliberately undersized fixtures override that shared size explicitly. This keeps the expected visual distinction clear and avoids placing borderline artwork on a probabilistic model's `smaller`/`similar` boundary. Gemini is instructed to return `smaller` only for a clear visible difference; close or modest differences are reported as `similar`.

## Proof-of-concept boundary

This is a proof-of-concept application. It uses generated test data to demonstrate successful applications and each supported failure condition. The generated PDFs, label images, source data, expected outcomes, and expected findings are retained for manual inspection so an evaluator can verify that each example matches the criterion it is intended to test.

The prototype targets current electronic Form 5100.31 submissions whose form data can be extracted directly. It is not intended to process a handwritten form that has been scanned, or a form that was printed and then scanned. Those inputs are possible extensions, but they require an additional document-recognition component. That component would need to convert the scanned form into the same application JSON contract and extract the label images for the external image provider. Because the parser boundary is defined by those outputs, adding such an intake adapter would not require changing the validation engine.

## Roadmap and decisions to date

### Step 1: Review the assignment and current TTB materials

The assignment described routine matching between application data and label artwork and encouraged review of current TTB guidance. We therefore checked the current application rather than building around the supplied 2006 example.

The current Form 5100.31 revision is 04/2023. It no longer provides the former application-level ABV field. TTB's change notice explains that alcohol content and net contents were removed from the application because the label images already display them. The application cannot compare label ABV against a value that the current form does not collect. The design records this scope change instead of silently assuming the obsolete form structure.

ABV remains label data. It is not compared against Form 5100.31, because the current form does not collect it. The validator instead applies the beverage-specific rules that are currently supported:

- Under [27 CFR 5.143(a)](https://www.ecfr.gov/current/title-27/chapter-I/subchapter-A/part-5/subpart-I/section-5.143), whisky must be bottled at not less than 40 percent alcohol by volume.
- Under [27 CFR 4.21(a)(6)](https://www.ecfr.gov/current/title-27/chapter-I/subchapter-A/part-4/subpart-C/section-4.21), dessert wine must contain more than 14 percent and not more than 24 percent alcohol by volume.

The synthetic corpus includes a whisky labeled at 38 percent ABV and a dessert wine labeled at 25 percent ABV. Both are expected rejections. Other beverage-specific ABV limits will remain unevaluated until an authoritative rule is added to the validator's rules file.

### Step 2: Build deterministic synthetic applications

We created a configurable generator whose input JSON contains application values and label content. It fills the current form, renders front and back label images, attaches them to the form, and records exact expected observations.

Separating the generator from its data allows the generator code to remain stable while new regulatory and failure cases are added through JSON. It also supports reproducible tests: the intended difference in each case is declared rather than inferred from an opaque image.

### Step 3: Parse the application and isolate labels

The initial Python pipeline established that the fillable PDF fields and attached label areas could be extracted. The parser was then implemented as a command-line C# application so it can be called by a future frontend without embedding user-interface concerns.

For each PDF, the parser writes:

- a copy of the source PDF;
- extracted application JSON with source-field evidence;
- zero or more rendered label PNGs;
- a structured Gemini request, response schema, instructions, and request manifest.

The parser defaults to preparing the request without sending it. Live sending requires an explicit flag and a runtime API key.

### Step 4: Delegate label-image transcription

Extracting label artwork from the application PDF is part of this implementation. Building and training a custom OCR or computer-vision system is outside the scope of this prototype. The application delegates transcription of label pixels to Gemini through a narrow structured interface, while retaining responsibility for document parsing, validation rules, evidence, error handling, and human review.

Gemini is an interchangeable provider, not part of the validation policy. A different multimodal model, OCR vendor, or internal government service could implement the same image-in/JSON-out contract. The validation engine receives structured observations and decides how they apply to the application; the image provider does not approve or reject a label.

This boundary keeps the prototype focused on its application-specific work:

- extracting application data and label images from Form 5100.31;
- defining a stable structured extraction contract;
- comparing application and label observations;
- applying TTB validation rules;
- preserving evidence and uncertainty for human review;
- supporting both live and synthetic extraction responses.

The provider boundary also acknowledges production concerns outside this prototype: procurement, model accuracy, latency, cost, privacy, retention, network access, and federal security requirements.

### Step 5: Validate the extraction contract

Five applications were sent to `gemini-3.1-flash-lite`: a conforming baseline, an altered warning, a mixed-case heading, a very small warning, and a subtly undersized warning. Gemini correctly transcribed the warning wording and identified heading capitalization and bolding in all five cases. It also produced several ordinary-field interpretations that will need to be handled by the validation layer, reinforcing the decision to keep compliance policy outside the prompt.

A follow-up relative-size test used one controlled label whose warning body was rendered at the same pixel font size as its producer/address and contents text and one label with a very small warning. The extraction instruction explicitly identified those ordinary informational fields as the comparison reference and excluded brand, fanciful-name, class/type, decorative, and heading text. Gemini returned `similar` for the controlled equal-size case and `smaller` for the very-small case. This supports the relative visual heuristic while leaving physical-size compliance unevaluated.

To make validation-engine development deterministic, live responses were preserved separately and fixture-backed responses were created using the same response envelope and structured field contract. The bundled offline demonstration uses fixture-backed responses for all 60 cases, so its expected 30 approvals and 30 rejections do not vary with earlier model output. Synthetic responses are explicitly marked and cannot be confused with live model results.

### Step 6: Build the validation engine

The C# validation engine accepts the application extraction and label extraction as separate inputs. It normalizes values, applies beverage- and import-specific applicability rules, compares application and label observations, evaluates the warning, and emits a disposition plus structured findings and evidence.

The engine isolates Gemini's response envelope in a small adapter that converts it into the stable label-observation model. Provider output is treated as untrusted input: the adapter validates required objects, fields, statuses, evidence, booleans, and the relative-size enumeration before any compliance rule runs. An absent, malformed, or unusable provider result produces `external_service_unavailable`.

Validation policy is stored in versioned JSON rather than embedded throughout the C# implementation. The rules file defines:

- application and label fields that must be compared when the field exists in both sources;
- fields required on the label and the conditions under which they apply;
- supported beverage-class ABV boundaries, including whether each boundary is inclusive or exclusive;
- the exact government-warning heading and body text;
- government-warning capitalization, bolding, and relative-presentation requirements;
- normalization permitted before comparison;
- stable finding codes and user-facing explanations.

The C# engine loads and validates the rules file at startup. Invalid or incomplete rule configuration is a configuration error, not an application rejection. It evaluates every applicable rule and returns all findings for the application so a multi-failure submission gives the user a complete explanation in one run.

“Match all fields” means compare every relevant field represented in both the application extraction and the label extraction. Fields found only on the label, such as ABV and net contents on the current form, receive presence and regulatory checks but cannot be compared with an application value that does not exist.

Wine appellation is included in that comparison contract. Form 5100.31 exposes the application appellation directly, and Gemini transcribes the corresponding geographic statement from the label. A difference such as `Paso Robles` on the application and `Mendoza` on the label produces a configured `WINE_APPELLATION_MISMATCH` finding. This check also prevents a geographic place name from silently satisfying a different field such as country of origin.

Comparison normalization will ignore capitalization, repeated whitespace, line breaks, and non-substantive punctuation. Producer comparison will also remove label role prefixes such as `Produced and bottled by`; the producer name, street number, street name, city, state, and postal code remain significant. Brand comparison will accept the application brand by itself or the application brand followed by its fanciful name because image transcription may return those adjacent label elements as one value. Additional or changed substantive brand words remain a mismatch; the engine will not use unrestricted fuzzy matching.

The initial ABV rules deliberately cover whisky and dessert wine only. Their purpose is to demonstrate that regulatory boundaries are external, configurable rules rather than to catalogue every alcohol-content regulation for every beverage class. Additional rules can be added to the JSON policy without changing the validation engine.

The image-extraction contract represents apparent government-warning size as `smaller`, `similar`, `larger`, or `undetermined` relative to other informational text on the label. `smaller` is a deterministic warning failure. `undetermined` means the provider could not make the observation and requires manual review. This remains a relative visual test and does not claim to measure physical type size.

The complete generated corpus validates 60 applications: all 30 intended-pass cases return `approve`, all 30 deterministic failure cases return `reject`, and every actual finding-code set matches its fixture. The six-failure case returns all six reasons. Separate integration tests exercise all five public statuses, including `manual_review`, `malformed_input`, and `external_service_unavailable`.

## Physical type size cannot be established from unscaled artwork

TTB warning requirements use physical measurements, including minimum character heights of 1 mm, 2 mm, or 3 mm depending on container capacity and maximum characters per inch. The label attachments available to this prototype are raster images embedded in an application PDF. Their pixels do not establish the dimensions at which the label will be printed.

Artwork may be resized, resampled, scanned, photographed, or scaled when inserted into the PDF. A PDF bounding box describes how large the image appears on a PDF page; it does not prove the physical size of the final printed label. Image DPI metadata is also insufficient because it may be changed or discarded without changing the underlying artwork.

Consequently, this prototype must not claim to approve or reject the physical 1/2/3 mm requirement or characters-per-inch limit from label pixels alone. It can evaluate apparent size relative to other visible label text as a stakeholder-driven prototype heuristic. The two synthetic small-warning cases test extraction reliability and apparent presentation only; they do not demonstrate automated measurement of regulatory type size.

Physical-size validation requires authoritative scale information, such as verified printed-label dimensions, trustworthy submission metadata, or a requirement that artwork be submitted at a defined scale. Until that information is available, the application should report physical type-size compliance as **not assessable from the submitted artifacts**.

Sending the same PDF and raster images to a human reviewer does not resolve the missing measurement. A reviewer could judge ordinary readability and relative presentation, but could not determine legal character height or characters per inch from unscaled artwork. Resolving physical-size compliance requires additional evidence, such as actual label dimensions, a print proof, or authoritative scale metadata. The prototype should disclose this limitation without treating the unavailable physical measurement itself as a pass, rejection, or resolvable manual-review finding.

## Frontend and deployment direction

The assignment permits any language or framework, notes a .NET/Azure environment, and separately requires both a source repository and a deployed application URL. ASP.NET Core is therefore a suitable frontend choice and aligns with the existing C# components.

This proof of concept is deliberately deployed as a self-contained local ASP.NET Core application at `http://127.0.0.1:5080`. The evaluator receives an installed application and a stable deployment URL on the evaluator's machine. Preparing, securing, funding, and operating a public cloud environment would consume a disproportionate share of the limited prototype schedule without improving the application-specific parsing and validation demonstration.

The launcher does not automatically invoke the system's configured default browser. Two development launches caused that browser process to fail before rendering the page, while the local ASP.NET server continued normally. The installation instructions therefore tell the evaluator to open the stable URL in a browser of their choice; an explicit `--open-browser` option remains available for environments where shell browser launch is reliable.

Local deployment also gives the evaluator a more representative measurement of the requirement that ordinary applications complete in approximately five seconds. The displayed total includes parsing, the evaluator's real network path to Gemini, Gemini processing, and local validation on the machine where the prototype is being assessed. The UI displays total and Gemini time; parser, validator, retry, and attempt timings remain in the result data for diagnosis.

The tradeoff is explicit: `127.0.0.1` is a deployment URL accessible on the installed machine, not a public Internet URL. A production deployment would require an approved hosting environment, centrally managed provider credentials, approved outbound access to the selected image-processing service, monitoring, and operational ownership. Production users should not create or handle individual Gemini API keys.

For live prototype processing, the user enters a Gemini API key in a masked field and tests the key and outbound connection before file selection is enabled. The key is retained only in server process memory for the session. It is not written to configuration, logs, result files, generated requests, or browser storage. The application explains that this is a prototype constraint and links to instructions for obtaining a free test key; actually obtaining the key remains outside the application.

The frontend accepts one to 300 PDF applications, processes at most two Gemini calls concurrently to respect free-tier capacity, keeps completed results when another file fails, and supports cancellation of queued work. Each file reports one of the five validator statuses and preserves all findings. The summary reports counts for approved, rejected, manual-review, malformed-input, service-unavailable, and cancelled files.

The parser owns Gemini transmission and retry behavior because provider communication belongs to extraction, not presentation. It makes at most four attempts for network failures, timeouts, and HTTP 429, 500, 502, 503, or 504 responses with jittered exponential backoff. The request manifest records each attempt, HTTP status, duration, overall Gemini duration, and final error. A failure after the final attempt becomes `external_service_unavailable` downstream. Provider congestion can make the five-second target unattainable; that time is reported as Gemini latency rather than hidden or attributed to validation.

An offline button labeled **Run bundled demonstration using synthetic responses** processes only the bundled test corpus. It does not accept arbitrary PDFs or call Gemini. Every such result is visibly marked **Synthetic extraction**, preventing fixture-backed results from being mistaken for live provider output.

The frontend remains a standalone proof of concept. Direct integration with the production COLA system is explicitly outside the assignment scope. Writing an approval into an output copy of the PDF may be useful, but it is an additional workflow decision rather than an explicit assignment requirement.

## Remaining implementation decisions

- Benchmark live end-to-end latency over a larger request set and document percentile results in addition to individual elapsed times.
- Determine production retention, monitoring, identity, accessibility, and hosting requirements if the proof of concept advances beyond local evaluation.
