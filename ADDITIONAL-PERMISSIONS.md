## Network Connections, AI Services, and Privacy

DBVO Studio is a Windows application for Skyrim Special Edition that helps authors create Dragonborn Voice Over (DBVO) voice packs and revoice NPCs.

**DBVO Studio does not have an automatic updater, telemetry, or an account system operated by the developer.** It only makes network connections for features that users choose to use.

### Network Connections

The application may communicate with the following services, depending on the features the user enables and configures:

- **ElevenLabs API:** Generates voice lines using the user's own ElevenLabs API key. This is a core voice-generation feature.
- **Hugging Face:** Downloads a Whisper speech-recognition model for transcription. The model is downloaded on first use of the WAV-to-LIP workflow and can then be reused locally.
- **Optional AI provider:** Suggests delivery tags for dialogue lines. The user may configure a local provider, such as Ollama or LM Studio, or an online provider, such as OpenAI or Anthropic, using their own credentials.
- **Optional local voice servers:** Connects to VoxCPM or OmniVoice through localhost when the user runs and selects those services.

The application does not operate a developer-hosted account or telemetry service. Online providers process requests under their own terms and privacy policies. The information sent to a provider depends on the feature used and the request made.

### API Key Storage

API keys are stored encrypted for the current Windows user account and are sent to the service to which they belong when the corresponding feature is used.

Users should protect their API keys and review the privacy, retention, and usage policies of any online provider they choose.

### Third-Party Components

The distributed application includes the following third-party components:

- **FFmpeg:** Included unmodified under its applicable LGPL licence and notices.
- **7-Zip:** Included unmodified under its applicable LGPL licence and notices.

The distribution includes the applicable licence information and source links for these components.

The following tools are not bundled and must be obtained separately by the user:

- FaceFXWrapper
- FonixData.cdf
- xWMAEncode

These tools are associated with the Skyrim Creation Kit toolchain and are subject to their own distribution terms.

### AI-Generated Content

DBVO Studio can be used to generate voice audio and to assist with dialogue tagging through AI services. The content created depends on the services and settings chosen by the user.

Authors publishing content made with DBVO Studio are responsible for accurately disclosing AI-generated or AI-assisted content, applying the appropriate platform tags, and complying with the terms of the services they use.

### Nexus Mods Submission

DBVO Studio makes network connections for functionality that depends on downloading models or communicating with user-selected services. Nexus Mods' File Submission Guidelines specifically address software that connects to the internet and recommend contacting staff with the reasoning and source code for review.

The developer has sought clarification from Nexus Mods before submitting the application. Acceptance remains subject to Nexus Mods' review and applicable policies.

DBVO Studio is an independent community tool and is not affiliated with or endorsed by Bethesda Softworks, Nexus Mods, ElevenLabs, Hugging Face, OpenAI, or Anthropic.
