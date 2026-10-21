"""Extract the reference data DBVO Studio uses from a UESP Skyrim corpus dump (uesp_skyrim_articles.jsonl)."""
import json, re, sys, collections, hashlib

src, out = sys.argv[1], sys.argv[2]
MOD_PLUGIN = {'Dawnguard': 'Dawnguard.esm', 'Dragonborn': 'Dragonborn.esm', 'Hearthfire': 'HearthFires.esm'}

def field(w, name):
    m = re.search(r'\n\|\s*' + name + r'\s*=\s*([^\n]*)', w, re.I)
    return m.group(1).strip() if m else ''

def unlink(t):
    t = re.sub(r'\[\[[^|\]]*\|([^\]]*)\]\]', r'\1', t)
    t = re.sub(r'\[\[([^\]]*)\]\]', lambda m: m.group(1).split(':', 1)[-1], t)
    t = re.sub(r'\{\{[^}]*\}\}', '', t)
    t = re.sub(r"<[^>]+>|'''?", '', t)
    return re.sub(r'\s+', ' ', t).strip()

def link_target(t):
    m = re.search(r'\[\[([^|\]#]+)', t)
    return m.group(1).strip() if m else ''

def plugin_of(w):
    m = re.search(r'\{\{Mod Header\|([^}|]+)', w)
    if m: return MOD_PLUGIN.get(m.group(1).strip(), '')
    if '{{CC Header' in w or '{{Creation Header' in w: return ''
    return 'Skyrim.esm'

def form_id(v):
    v = v.strip().split()[0] if v.strip() else ''
    m = re.fullmatch(r'(?:xx|[0-9A-Fa-f]{2})([0-9A-Fa-f]{6})', v)
    return m.group(1).upper() if m else ''

pages = []
revs = []
for line in open(src, encoding='utf-8'):
    d = json.loads(line)
    pages.append(d)
    revs.append(d.get('revision_timestamp', ''))

npcs, books = [], []
for d in pages:
    w, title = d['content_wikitext'], d['title']
    if not title.startswith('Skyrim:'): continue
    name = title.split(':', 1)[1]
    if '{{NPC Summary' in w:
        plugin = plugin_of(w)
        npcs.append({'name': re.sub(r'\s*\((?:[^)]*)\)$', '', name), 'page': title, 'url': d['source_url'], 'plugin': plugin,
                     'baseId': form_id(field(w, 'baseid')), 'refId': form_id(field(w, 'refid')),
                     'voiceType': unlink(field(w, 'voicetype')), 'race': unlink(field(w, 'race')), 'gender': unlink(field(w, 'gender')),
                     '_w': w})
    if '{{Game Book' in w:
        a = field(w, 'author')
        books.append({'title': name, 'url': d['source_url'], 'plugin': plugin_of(w), 'formId': form_id(field(w, 'id')),
                      'author': unlink(a), 'authorPage': link_target(a), 'found': unlink(field(w, 'loc')).lstrip('*').strip(),
                      'quest': unlink(field(w, 'quest'))})

# Quotes: ''"..."'' on NPC pages. A '''Name''': prefix on the same line names the speaker (conversations quote both sides).
by_first = collections.defaultdict(set)
for i, n in enumerate(npcs):
    by_first[n['name'].lower()].add(i)
    by_first[n['name'].split()[0].lower()].add(i)
quotes = collections.defaultdict(lambda: {'speakers': set(), 'labels': set()})
QUOTE = re.compile(r"''\"(.+?)\"''")
for i, n in enumerate(npcs):
    first = n['name'].split()[0].lower()
    for ln in n['_w'].split('\n'):
        qs = QUOTE.findall(ln)
        if not qs: continue
        m = re.match(r"\s*[:*#]*\s*'''([^']+?)'''\s*:", ln)
        label = unlink(m.group(1)) if m else ''
        if label and label.lower() not in (n['name'].lower(), first):
            cand = by_first.get(label.lower(), set())
            who = next(iter(cand)) if len(cand) == 1 else None
        else:
            who = i
        for q in qs:
            text = unlink(q)
            if len(re.sub(r'[^a-z0-9]', '', text.lower())) < 12: continue
            e = quotes[text]
            if who is not None: e['speakers'].add(who)
            else: e['labels'].add(label)
for n in npcs: del n['_w']

data = {
    'source': {
        'name': 'UESP — The Unofficial Elder Scrolls Pages', 'url': 'https://en.uesp.net/',
        'license': 'CC BY-SA 2.5', 'licenseUrl': 'https://creativecommons.org/licenses/by-sa/2.5/',
        'pages': len(pages), 'revisionsFrom': min(r for r in revs if r), 'revisionsTo': max(revs),
        'note': 'Derived from UESP wiki pages: NPC summaries, quoted dialogue and Game Book summaries. Text is adapted (wiki markup removed).',
    },
    'npcs': npcs,
    'quotes': [{'text': t, 'npcs': sorted(e['speakers']), **({'labels': sorted(e['labels'])} if e['labels'] else {})} for t, e in sorted(quotes.items())],
    'books': books,
}
json.dump(data, open(out, 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
print(f"npcs {len(npcs)} (voice type {sum(1 for n in npcs if n['voiceType'])}, base id {sum(1 for n in npcs if n['baseId'])}); quotes {len(data['quotes'])} (attributed {sum(1 for q in data['quotes'] if q['npcs'])}); books {len(books)} (author {sum(1 for b in books if b['author'])}, form id {sum(1 for b in books if b['formId'])})")
