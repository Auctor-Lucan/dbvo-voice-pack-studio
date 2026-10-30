// Voice engines: names, how each uses [tags], and the toolbar controls of the cloud engines added in 9.0
// (Fish Audio, Inworld, Cartesia, MiniMax, OpenAI-compatible). Shared by Phase 2, NPC Revoice, Voiced Books, Player
// Grunts and the voice pack editor, so every place says the same thing.
import { h } from './lib.js';

export const ENGINES = [
  ['elevenlabs', 'ElevenLabs'],
  ['fish', 'Fish Audio'],
  ['inworld', 'Inworld'],
  ['cartesia', 'Cartesia'],
  ['minimax', 'MiniMax'],
  ['openai', 'OpenAI-compatible'],
  ['omnivoice', 'OmniVoice (local)'],
  ['voxcpm', 'VoxCPM (local)'],
];
export const ENGINE_NAMES = Object.fromEntries(ENGINES);
export const CLOUD = ['fish', 'inworld', 'cartesia', 'minimax', 'openai'];
export const isCloud = (e) => CLOUD.includes(e);

/** An address on this PC or the local network (an OpenAI-compatible server there needs no key); same test as the app's. */
export function isLocalUrl(url) {
  let t = (url || '').trim();
  if (t && !t.includes('://')) t = 'http://' + t;
  try {
    const h = new URL(t).hostname.replace(/^\[|\]$/g, '');
    return h === 'localhost' || h === '::1' || /^127\./.test(h) || h.endsWith('.local') || /^192\.168\./.test(h) || /^10\./.test(h) || /^172\.(1[6-9]|2[0-9]|3[01])\./.test(h);
  } catch { return false; }
}

/** The voice pack's settings object for an engine (JSON names: fish, inworld, cartesia, miniMax, openAi …). */
export function engineObj(voice, eng) {
  return { elevenlabs: voice.eleven, voxcpm: voice.voxCpm, omnivoice: voice.omniVoice, fish: voice.fish, inworld: voice.inworld, cartesia: voice.cartesia, minimax: voice.miniMax, openai: voice.openAi }[eng];
}

/** Defaults of the cloud engines' settings (as the app saves them), for voice packs made before 9.0. */
export const ENGINE_DEFAULTS = {
  fish: { voiceId: '', voiceName: '', model: 's2.1-pro', speed: 1, temperature: 0, concurrency: 2 },
  inworld: { voiceId: '', voiceName: '', modelId: 'inworld-tts-2', speakingRate: 1, concurrency: 2 },
  cartesia: { voiceId: '', voiceName: '', modelId: 'sonic-3.6', language: 'en', speed: 1, volume: 1, emotion: '', concurrency: 2 },
  miniMax: { voiceId: '', voiceName: '', model: 'speech-2.8-hd', speed: 1, volume: 1, pitch: 0, emotion: '', concurrency: 2 },
  openAi: { baseUrl: 'https://api.openai.com', model: 'gpt-4o-mini-tts', voice: 'marin', instructions: '', speed: 1, extraJson: '', concurrency: 2 },
};

export const CARTESIA_EMOTIONS = ['neutral', 'happy', 'excited', 'enthusiastic', 'elated', 'euphoric', 'triumphant', 'amazed', 'surprised', 'flirtatious',
  'curious', 'content', 'peaceful', 'serene', 'calm', 'grateful', 'affectionate', 'trust', 'sympathetic', 'anticipation',
  'mysterious', 'angry', 'mad', 'outraged', 'frustrated', 'agitated', 'threatened', 'disgusted', 'contempt', 'envious',
  'sarcastic', 'ironic', 'sad', 'dejected', 'melancholic', 'disappointed', 'hurt', 'guilty', 'bored', 'tired', 'rejected',
  'nostalgic', 'wistful', 'apologetic', 'hesitant', 'insecure', 'confused', 'resigned', 'anxious', 'panicked', 'alarmed',
  'scared', 'proud', 'confident', 'distant', 'skeptical', 'contemplative', 'determined'];
export const MINIMAX_EMOTIONS = ['happy', 'sad', 'angry', 'fearful', 'disgusted', 'surprised', 'calm', 'fluent', 'whisper'];

/** One line on what an engine does with [tags]: shown next to tag bars and in the toolbar. */
export function tagNote(eng, { tagModel = true, model = '' } = {}) {
  switch (eng) {
    case 'elevenlabs': return tagModel ? 'v3 and v4 models perform [tags] anywhere in the line: emotions, accents, breaths, laughs, shouts…' : `${model || 'This model'} ignores [tags] (they are spoken as words or dropped); pick a v3 or v4 model to use them.`;
    case 'fish': return 'Fish Audio S2 performs free-form [tags] anywhere in the line, like ElevenLabs v3/v4 ([sad], [whispering], [British accent]…). They count as text you pay for.';
    case 'inworld': return 'Inworld TTS-2 performs free-form [tags] as instructions from where they stand until the next one ([reset] stops them), and sounds such as [laugh] [sigh] [breathe]. The flash model only does the sounds. They count as text you pay for.';
    case 'cartesia': return 'Cartesia doesn\'t read [tags]: they are left out of the text. A leading tag that names an emotion (or a word close to one, e.g. [Baleful] → contempt) sets the line\'s emotion; [laughter] / [laughs] is kept as a laugh. English only.';
    case 'minimax': return 'MiniMax doesn\'t read [tags]: they are left out. A leading tag picks one of 9 emotions (happy, sad, angry, fearful, disgusted, surprised, calm; fluent and whisper on speech-2.6). On speech-2.8, [laughs] [sighs] [gasps] [chuckle] [coughs] become sounds.';
    case 'openai': return 'OpenAI-compatible engines don\'t read [tags]: they are left out of the text, and their words are sent as delivery instructions ("Deliver this line: British, Extremely Baleful"). gpt-4o-mini-tts follows them; most local servers ignore them.';
    case 'omnivoice': return 'OmniVoice performs [laughter] and [sigh] ([laughs] and [sighs] work too). Other tags are left out, as it would read them aloud.';
    case 'voxcpm': return 'VoxCPM reads [tags] aloud, so they are left out of the text. Punctuation, … and CAPS still shape the delivery.';
    default: return '';
  }
}

/** Where to find the voice ID, per engine (shown as the voice field's tooltip and in the guide). */
export const VOICE_HINT = {
  fish: 'The voice model\'s ID from fish.audio (your model\'s page → the ID in its address, or "Copy ID"). Make the model there from your own recordings.',
  inworld: 'A voice ID from Inworld\'s TTS playground or your cloned voices (e.g. Ashley, or the ID of a cloned voice).',
  cartesia: 'A voice ID from play.cartesia.ai → Voices (or a voice you cloned there): the long ID, not the name.',
  minimax: 'A system voice ID (e.g. English_expressive_narrator) or the ID of a voice you cloned at platform.minimax.io.',
  openai: 'OpenAI: a built-in voice (marin, cedar, alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer, verse). A local server: the voice name it lists.',
};

/**
 * Toolbar controls for a cloud engine's settings. helpers: { grp, numIn, setIn, locked } from the page;
 * txt and pick are made here so every page builds the same controls.
 */
export function cloudControls(eng, o, opts) { return controlsOf(eng, o, opts).filter(Boolean); }
function controlsOf(eng, o, { grp, numIn, setIn, locked, noVoice = false }) {
  const txt = (key, label, opts = {}) => {
    if (noVoice && (key === 'voiceId' || key === 'voice')) return null; // the voice is picked elsewhere (NPC Revoice speakers, Voiced Books)
    const i = h('input', { type: 'text', value: o[key] ?? '', placeholder: opts.placeholder || '', disabled: locked, title: opts.title || '', style: { width: opts.width || '150px' } });
    i.addEventListener('change', () => { if (key === 'voiceId') o.voiceName = ''; setIn(o, key, i.value.trim()); }); // a typed ID replaces any remembered name
    return grp(label, i);
  };
  const pick = (key, label, options, opts = {}) => grp(label, h('select', { disabled: locked, title: opts.title || '', onchange: (ev) => setIn(o, key, ev.target.value) },
    options.map(([v, t]) => h('option', { value: v, selected: v === (o[key] ?? '') }, t))));
  const conc = numIn(o, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'How many lines are requested at once (your plan\'s limit)' });
  switch (eng) {
    case 'fish': return [
      txt('voiceId', 'Voice ID', { placeholder: 'voice model ID', title: VOICE_HINT.fish, width: '190px' }),
      pick('model', 'Model', [['s2.1-pro', 's2.1-pro'], ['s2-pro', 's2-pro']], { title: 'S2 models perform free-form [tags]' }),
      numIn(o, 'speed', 'Speed', { step: 0.05, title: 'Speaking speed (1 = normal)' }),
      numIn(o, 'temperature', 'Temperature', { step: 0.05, title: 'Variation between takes; 0 = Fish Audio\'s default' }), conc];
    case 'inworld': return [
      txt('voiceId', 'Voice ID', { placeholder: 'e.g. Ashley', title: VOICE_HINT.inworld }),
      pick('modelId', 'Model', [['inworld-tts-2', 'inworld-tts-2 · tags'], ['inworld-tts-2-flash', 'inworld-tts-2-flash']], { title: 'inworld-tts-2 follows [tags] as instructions; flash only performs sounds such as [laugh]' }),
      numIn(o, 'speakingRate', 'Speed', { step: 0.05, title: 'Speaking rate (1 = normal)' }), conc];
    case 'cartesia': return [
      txt('voiceId', 'Voice ID', { placeholder: 'voice ID', title: VOICE_HINT.cartesia, width: '190px' }),
      txt('modelId', 'Model', { placeholder: 'sonic-3.6', width: '100px', title: 'Cartesia model ID (sonic-3.6 by default)' }),
      txt('language', 'Language', { placeholder: 'en', width: '60px', title: 'Language (en) or locale (en-GB). Emotions work in English only.' }),
      numIn(o, 'speed', 'Speed', { step: 0.05, title: '0.6–1.5; Cartesia treats it as guidance' }),
      numIn(o, 'volume', 'Volume', { step: 0.05, title: '0.5–2.0' }),
      pick('emotion', 'Emotion', [['', 'from the tags'], ...CARTESIA_EMOTIONS.map((e) => [e, e])], { title: 'The emotion of lines whose leading tags name none. "From the tags" leaves those lines to the model.' }), conc];
    case 'minimax': return [
      txt('voiceId', 'Voice ID', { placeholder: 'voice ID', title: VOICE_HINT.minimax, width: '190px' }),
      pick('model', 'Model', [['speech-2.8-hd', 'speech-2.8-hd'], ['speech-2.8-turbo', 'speech-2.8-turbo'], ['speech-2.6-hd', 'speech-2.6-hd'], ['speech-2.6-turbo', 'speech-2.6-turbo']],
        { title: 'speech-2.8 turns [laughs] [sighs]… into sounds; speech-2.6 adds the fluent and whisper emotions' }),
      numIn(o, 'speed', 'Speed', { step: 0.05, title: '0.5–2 (1 = normal)' }), numIn(o, 'volume', 'Volume', { step: 0.1, title: '1 = normal' }),
      numIn(o, 'pitch', 'Pitch', { step: 1, title: '−12 to 12 semitones' }),
      pick('emotion', 'Emotion', [['', 'from the tags'], ...MINIMAX_EMOTIONS.map((e) => [e, e])], { title: 'The emotion of lines whose leading tags name none. "From the tags" leaves those lines to the model.' }), conc];
    case 'openai': return [
      txt('baseUrl', 'Server', { placeholder: 'https://api.openai.com', width: '180px', title: 'https://api.openai.com, or a local OpenAI-compatible server such as http://127.0.0.1:5123 (Chatterbox, Kokoro-FastAPI, AllTalk, openedai-speech). A server on this PC needs no key.' }),
      txt('model', 'Model', { placeholder: 'gpt-4o-mini-tts', width: '120px', title: 'gpt-4o-mini-tts on OpenAI; whatever the local server expects otherwise' }),
      txt('voice', 'Voice', { placeholder: 'marin', width: '100px', title: VOICE_HINT.openai }),
      numIn(o, 'speed', 'Speed', { step: 0.05 }),
      txt('instructions', 'Instructions', { placeholder: 'e.g. a cold, aristocratic vampire', width: '180px', title: 'Delivery instructions for every line; the words of each line\'s [tags] are added' }),
      txt('extraJson', 'Extra fields', { placeholder: '{"exaggeration": 0.6}', width: '140px', title: 'Optional JSON fields for local servers, sent as they are (Chatterbox: exaggeration, cfg_weight, temperature)' }), conc];
    default: return [];
  }
}

/** A badge saying whether the engine's API key is saved (Global Settings → General & tools → Voice engines). */
export function keyBadge(eng, state) {
  const has = state && state.engineKeys && state.engineKeys[eng];
  return h(has ? 'span.badge.b-valid' : 'span.badge.b-broken', { title: has ? 'API key saved' : 'No API key: add it in Global Settings → General & tools → Voice engines' }, has ? 'key ✓' : 'no key');
}

