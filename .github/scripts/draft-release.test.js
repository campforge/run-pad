// Mock-REST tests for draft-release.js. No network access, no real GitHub calls.
// Run: node --test .github/scripts/draft-release.test.js
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const helper = require('./draft-release.js');

const VERSION = '0.3.0';
const TAG = 'v0.3.0';
const SHA = 'abc123';
const RUN_ID = 4242;
// The mock serves list endpoints in pages of this size regardless of per_page, so every test with
// more than two releases/assets exercises pagination.
const PAGE_SIZE = 2;

function makeContext(overrides = {})
{
    return { repo: { owner: 'Tervforge', repo: 'run-pad' }, sha: SHA, runId: RUN_ID, ...overrides };
}

function makeCore()
{
    const messages = [];
    return { messages, info: (message) => messages.push(message), warning: (message) => messages.push(message) };
}

function httpError(status, message)
{
    const error = new Error(message);
    error.status = status;
    return error;
}

function makeGithub({ releases = [], tags = [] } = {})
{
    const state = { releases: [], assets: new Map(), tags: new Set(tags), nextId: 100, calls: [], fail: {} };

    function addRelease(release)
    {
        const full = { id: state.nextId++, draft: true, tag_name: TAG, target_commitish: SHA, body: '', ...release };
        state.releases.push(full);
        state.assets.set(full.id, []);
        return full;
    }
    releases.forEach(addRelease);

    function addAsset(releaseId, asset)
    {
        const full = { id: state.nextId++, state: 'uploaded', size: 10, ...asset };
        state.assets.get(releaseId).push(full);
        return full;
    }

    function call(name, params)
    {
        state.calls.push({ name, params });
        if (state.fail[name])
        {
            throw state.fail[name];
        }
    }

    function page(items, params)
    {
        const pageNumber = params.page || 1;
        return items.slice((pageNumber - 1) * PAGE_SIZE, pageNumber * PAGE_SIZE).map((item) => ({ ...item }));
    }

    const rest = {
        repos: {
            listReleases: async (params) =>
            {
                call('listReleases', params);
                return { data: page(state.releases, params) };
            },
            getRelease: async (params) =>
            {
                call('getRelease', params);
                const release = state.releases.find((candidate) => candidate.id === params.release_id);
                if (!release)
                {
                    throw httpError(404, 'Not Found');
                }
                return { data: { ...release } };
            },
            createRelease: async (params) =>
            {
                call('createRelease', params);
                const { owner, repo, ...fields } = params;
                return { data: { ...addRelease(fields) } };
            },
            updateRelease: async (params) =>
            {
                call('updateRelease', params);
                const release = state.releases.find((candidate) => candidate.id === params.release_id);
                Object.assign(release, { draft: params.draft });
                if (params.draft === false)
                {
                    state.tags.add(release.tag_name);
                }
                return { data: { ...release } };
            },
            listReleaseAssets: async (params) =>
            {
                call('listReleaseAssets', params);
                return { data: page(state.assets.get(params.release_id) || [], params) };
            },
            deleteReleaseAsset: async (params) =>
            {
                call('deleteReleaseAsset', params);
                for (const assets of state.assets.values())
                {
                    const index = assets.findIndex((asset) => asset.id === params.asset_id);
                    if (index >= 0)
                    {
                        assets.splice(index, 1);
                        return { data: undefined };
                    }
                }
                throw httpError(404, 'Not Found');
            },
            uploadReleaseAsset: async (params) =>
            {
                call('uploadReleaseAsset', params);
                const size = state.uploadSizeOverride ?? params.data.length;
                const uploadState = state.uploadStateOverride ?? 'uploaded';
                return { data: { ...addAsset(params.release_id, { name: params.name, size, state: uploadState }) } };
            },
        },
        git: {
            getRef: async (params) =>
            {
                call('getRef', params);
                if (!state.tags.has(params.ref.replace(/^tags\//, '')))
                {
                    throw httpError(404, 'Not Found');
                }
                return { data: { ref: `refs/${params.ref}` } };
            },
        },
    };

    async function paginate(method, params)
    {
        const all = [];
        for (let pageNumber = 1; ; pageNumber++)
        {
            const { data } = await method({ ...params, page: pageNumber });
            all.push(...data);
            if (data.length < PAGE_SIZE)
            {
                return all;
            }
        }
    }

    return { github: { rest, paginate }, state, addRelease, addAsset };
}

function ownedDraftFields(context = makeContext())
{
    return { draft: true, tag_name: TAG, target_commitish: context.sha, body: helper.ownershipMarker(context) };
}

function makeFiles(names, contents = 'payload')
{
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runpad draft release '));
    return names.map((name) =>
    {
        const file = path.join(dir, name);
        fs.writeFileSync(file, contents);
        return file;
    });
}

function callNames(state)
{
    return state.calls.map((entry) => entry.name);
}

const PLATFORM_FILES = [
    ['RunPad-win-x64.zip', 'RunPad-win-x64.msi'],
    ['RunPad-linux-x64.tar.gz', 'RunPad-x86_64.AppImage', `runpad_${VERSION}_amd64.deb`],
    ['RunPad-osx-x64.dmg', 'RunPad-osx-arm64.dmg'],
];

const WORKFLOW = path.join(__dirname, '../workflows/dotnet-desktop.yml');

// The `files: [...]` lists the workflow's uploadAssets steps pass, with ${version} substituted.
function workflowUploadFiles(workflowText, version)
{
    return [...workflowText.matchAll(/^\s*files: \[(.*)\],\s*$/gm)].map((match) =>
        [...match[1].matchAll(/'([^']*)'|`([^`]*)`/g)].map((file) =>
            (file[1] ?? file[2]).replace('${version}', version)));
}

test('readVersion parses RunPad.csproj without grep -P', () =>
{
    assert.equal(helper.readVersion('<Project>\r\n  <PropertyGroup>\r\n    <Version>0.3.0</Version>\r\n'), '0.3.0');
    assert.equal(helper.readVersionFromFile(path.join(__dirname, '../../src/main/RunPad/RunPad.csproj')).length > 0, true);
    assert.throws(() => helper.readVersion('<Project />'), /No <Version>/);
    assert.throws(() => helper.readVersion('<Version>1.0; rm -rf</Version>'), /Unexpected <Version>/);
});

test('readReleaseNotes takes the version section up to the next version or the link definitions', () =>
{
    const changelog = [
        '# Changelog', '', 'Intro.', '',
        '## [0.3.0] — 2026-10-12', '', '### Added', '', '- New thing, see [roadmap](ROADMAP.md).', '',
        '## [0.2.0] — 2026-07-18', '', '- Old thing.', '',
        '[0.3.0]: https://example.com/v0.3.0',
    ].join('\r\n');
    assert.equal(helper.readReleaseNotes(changelog, '0.3.0'), '### Added\n\n- New thing, see [roadmap](ROADMAP.md).');
    assert.equal(helper.readReleaseNotes(changelog, '0.2.0'), '- Old thing.');
    assert.throws(() => helper.readReleaseNotes(changelog, '0.4.0'), /no "## \[0\.4\.0\]" section/);
    assert.throws(() => helper.readReleaseNotes(changelog, '0.3'), /no "## \[0\.3\]" section/);
    assert.throws(() => helper.readReleaseNotes('## [1.0.0]\n\n## [0.9.0]\n- x', '1.0.0'), /is empty/);
});

test('readReleaseNotes joins wrapped lines, but not list items, headings or code', () =>
{
    const changelog = [
        '## [1.0.0]', '', 'A paragraph', 'wrapped here.', '',
        '- First item', '  continues.', '- Second item.', '', '### Fixed', '- Third.', '',
        '```text', 'code line', 'next line', '```',
    ].join('\n');
    assert.equal(helper.readReleaseNotes(changelog, '1.0.0'), [
        'A paragraph wrapped here.', '',
        '- First item continues.', '- Second item.', '', '### Fixed', '- Third.', '',
        '```text', 'code line', 'next line', '```',
    ].join('\n'));
});

test('CHANGELOG.md describes the version in RunPad.csproj', () =>
{
    const root = path.join(__dirname, '../..');
    const version = helper.readVersionFromFile(path.join(root, 'src/main/RunPad/RunPad.csproj'));
    assert.ok(helper.readReleaseNotesFromFile(path.join(root, 'CHANGELOG.md'), version).length > 0);
});

test('absolutizeLinks points repository-relative links at the released commit', () =>
{
    const notes = '[roadmap](ROADMAP.md), [code](src/main/RunPad), [site](https://example.com), [top](#added)';
    assert.equal(
        helper.absolutizeLinks(notes, makeContext()),
        '[roadmap](https://github.com/Tervforge/invoklet/blob/abc123/ROADMAP.md), ' +
        '[code](https://github.com/Tervforge/invoklet/blob/abc123/src/main/RunPad), ' +
        '[site](https://example.com), [top](#added)');
});

test('workflow uploads exactly the expected manifest, with macOS shipped as two DMGs', () =>
{
    const workflow = fs.readFileSync(WORKFLOW, 'utf8');
    const uploads = workflowUploadFiles(workflow, VERSION);
    assert.deepEqual(uploads.map((files) => files.map((file) => path.basename(file))), PLATFORM_FILES);

    const expected = helper.expectedAssetNames(VERSION);
    assert.equal(expected.length, 7);
    assert.deepEqual([...PLATFORM_FILES.flat()].sort(), [...expected].sort());
    assert.ok(!expected.some((name) => /osx.*\.tar\.gz$/.test(name)), 'macOS tarballs are gone');

    // Each uploaded DMG is the output of its own bundle step, built from that architecture's .app.
    for (const arch of ['x64', 'arm64'])
    {
        assert.ok(
            workflow.includes(`run: bash scripts/make-macos-dmg.sh RunPad-osx-${arch}/RunPad.app RunPad-osx-${arch}.dmg`),
            `bundle DMG step for osx-${arch}`);
        assert.ok(workflow.includes(`run: bash scripts/make-macos-app-bundle.sh publish/osx-${arch} RunPad-osx-${arch}`));
    }
    assert.ok(!/tar -czf RunPad-osx/.test(workflow), 'no macOS tar.gz archive step');
    assert.ok(workflow.indexOf('Bundle .app (osx-arm64)') < workflow.indexOf('Bundle DMG (osx-x64)'));
    assert.ok(workflow.indexOf('Bundle DMG (osx-arm64)') < workflow.indexOf('Upload to draft release (macOS)'));
});

test('happy path: draft created, all seven assets uploaded across jobs, then published', async () =>
{
    const context = makeContext();
    const core = makeCore();
    const mock = makeGithub({ releases: [{ draft: false, tag_name: 'v0.1.0' }, { draft: false, tag_name: 'v0.2.0' }] });

    const notes = '### Fixed\n\n- A bug, see [CONTRIBUTING](CONTRIBUTING.md).';
    const draft = await helper.prepareDraft({ github: mock.github, context, core, version: VERSION, notes });
    const created = mock.state.calls.find((entry) => entry.name === 'createRelease').params;
    assert.equal(created.draft, true);
    assert.equal(created.target_commitish, SHA);
    assert.equal(created.tag_name, TAG);
    assert.ok(created.body.startsWith(
        '### Fixed\n\n- A bug, see [CONTRIBUTING](https://github.com/Tervforge/invoklet/blob/abc123/CONTRIBUTING.md).'));
    assert.match(created.body, /source-sha=abc123 run-id=4242/);

    for (const names of PLATFORM_FILES)
    {
        await helper.uploadAssets({
            github: mock.github, context, core, releaseId: String(draft.id), version: VERSION, files: makeFiles(names),
        });
    }
    const uploads = mock.state.calls.filter((entry) => entry.name === 'uploadReleaseAsset');
    assert.equal(uploads.length, 7);
    assert.ok(uploads.every((entry) => entry.params.release_id === draft.id));
    assert.ok(uploads.every((entry) => entry.params.headers['content-length'] === 'payload'.length));

    await helper.publishDraft({ github: mock.github, context, core, releaseId: String(draft.id), version: VERSION });
    const update = mock.state.calls.find((entry) => entry.name === 'updateRelease').params;
    assert.equal(update.draft, false);
    assert.equal(update.release_id, draft.id);
    assert.equal(update.target_commitish, SHA);
    assert.equal(mock.state.releases.find((release) => release.id === draft.id).draft, false);
});

test('prepareDraft reuses the draft owned by the same run and commit (retry)', async () =>
{
    const context = makeContext();
    const mock = makeGithub({ releases: [{ draft: false, tag_name: 'v0.1.0' }, { draft: false, tag_name: 'v0.2.0' }] });
    const owned = mock.addRelease(ownedDraftFields(context));

    const draft = await helper.prepareDraft({ github: mock.github, context, core: makeCore(), version: VERSION });
    assert.equal(draft.id, owned.id);
    assert.ok(!callNames(mock.state).includes('createRelease'));
});

test('prepareDraft refuses a draft owned by another run or another commit', async () =>
{
    for (const other of [makeContext({ runId: 1 }), makeContext({ sha: 'def456' })])
    {
        const mock = makeGithub();
        mock.addRelease(ownedDraftFields(other));
        await assert.rejects(
            helper.prepareDraft({ github: mock.github, context: makeContext(), core: makeCore(), version: VERSION }),
            /belong to another commit or workflow run/);
        assert.ok(!callNames(mock.state).includes('createRelease'));
    }
});

test('prepareDraft fails clearly when the version is already published, without touching it', async () =>
{
    const published = makeGithub({ releases: [{ draft: false, tag_name: 'v0.1.0' }, { draft: false, tag_name: 'v0.2.0' },
        { draft: false, tag_name: TAG }] });
    await assert.rejects(
        helper.prepareDraft({ github: published.github, context: makeContext(), core: makeCore(), version: VERSION }),
        /already published.*Bump <Version>/s);

    const tagOnly = makeGithub({ tags: [TAG] });
    await assert.rejects(
        helper.prepareDraft({ github: tagOnly.github, context: makeContext(), core: makeCore(), version: VERSION }),
        /already published/);

    for (const mock of [published, tagOnly])
    {
        const mutations = callNames(mock.state).filter((name) => /create|update|delete|upload/.test(name));
        assert.deepEqual(mutations, []);
    }
});

test('uploadAssets on retry replaces only same-name assets of the owned draft, after listing every page', async () =>
{
    const context = makeContext();
    const mock = makeGithub();
    const owned = mock.addRelease(ownedDraftFields(context));
    const otherDraft = mock.addRelease({ ...ownedDraftFields(makeContext({ runId: 1 })), tag_name: 'v9.9.9' });
    // Five assets → three pages; the Linux ones sit on pages 1 and 3.
    const oldTar = mock.addAsset(owned.id, { name: 'RunPad-linux-x64.tar.gz', size: 3 });
    const winZip = mock.addAsset(owned.id, { name: 'RunPad-win-x64.zip' });
    const winMsi = mock.addAsset(owned.id, { name: 'RunPad-win-x64.msi' });
    const macDmg = mock.addAsset(owned.id, { name: 'RunPad-osx-x64.dmg' });
    const oldAppImage = mock.addAsset(owned.id, { name: 'RunPad-x86_64.AppImage', state: 'starter', size: 0 });
    const foreign = mock.addAsset(otherDraft.id, { name: 'RunPad-linux-x64.tar.gz' });

    await helper.uploadAssets({
        github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION, files: makeFiles(PLATFORM_FILES[1]),
    });

    const names = callNames(mock.state);
    const lastList = names.lastIndexOf('listReleaseAssets');
    const firstDelete = names.indexOf('deleteReleaseAsset');
    assert.equal(names.filter((name) => name === 'listReleaseAssets').length, 3);
    assert.ok(lastList < firstDelete, 'all pages are collected before deleting');
    const deleted = mock.state.calls.filter((entry) => entry.name === 'deleteReleaseAsset').map((entry) => entry.params.asset_id);
    assert.deepEqual(deleted.sort(), [oldTar.id, oldAppImage.id].sort());

    const remaining = mock.state.assets.get(owned.id).map((asset) => asset.id);
    for (const kept of [winZip, winMsi, macDmg])
    {
        assert.ok(remaining.includes(kept.id));
    }
    assert.deepEqual(mock.state.assets.get(otherDraft.id).map((asset) => asset.id), [foreign.id]);
});

test('uploadAssets refuses releases not owned by this run/commit and published releases', async () =>
{
    const context = makeContext();
    const cases = [
        { ...ownedDraftFields(makeContext({ runId: 1 })) },
        { ...ownedDraftFields(makeContext({ sha: 'def456' })) },
        { ...ownedDraftFields(context), target_commitish: 'main' },
        { ...ownedDraftFields(context), draft: false },
        { ...ownedDraftFields(context), tag_name: 'v0.2.0' },
    ];
    for (const fields of cases)
    {
        const mock = makeGithub();
        const release = mock.addRelease(fields);
        await assert.rejects(
            helper.uploadAssets({
                github: mock.github, context, core: makeCore(), releaseId: release.id, version: VERSION,
                files: makeFiles(PLATFORM_FILES[0]),
            }),
            /refusing to modify it/);
        assert.ok(!callNames(mock.state).some((name) => /delete|upload/.test(name)));
    }
});

test('uploadAssets rejects missing or empty files and invalid ids before any mutation', async () =>
{
    const context = makeContext();
    const mock = makeGithub();
    const owned = mock.addRelease(ownedDraftFields(context));
    const [empty] = makeFiles(['RunPad-win-x64.zip'], '');

    await assert.rejects(
        helper.uploadAssets({ github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION, files: [empty] }),
        /is empty/);
    await assert.rejects(
        helper.uploadAssets({
            github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION,
            files: [path.join(os.tmpdir(), 'runpad-no-such-file.zip')],
        }),
        /does not exist/);
    await assert.rejects(
        helper.uploadAssets({ github: mock.github, context, core: makeCore(), releaseId: '', version: VERSION, files: [empty] }),
        /Invalid draft release id/);
    assert.ok(!callNames(mock.state).some((name) => /delete|upload/.test(name)));
});

test('uploadAssets fails when the uploaded asset is incomplete', async () =>
{
    const context = makeContext();
    for (const override of [{ uploadSizeOverride: 1 }, { uploadStateOverride: 'starter' }])
    {
        const mock = makeGithub();
        Object.assign(mock.state, override);
        const owned = mock.addRelease(ownedDraftFields(context));
        await assert.rejects(
            helper.uploadAssets({
                github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION, files: makeFiles(PLATFORM_FILES[0]),
            }),
            /is incomplete/);
    }
});

test('publishDraft does not publish with missing, empty, unfinished, duplicated or unexpected assets', async () =>
{
    const context = makeContext();
    const expected = helper.expectedAssetNames(VERSION);
    const cases = [
        { name: 'missing', assets: expected.slice(1).map((name) => ({ name })), error: /missing: RunPad-win-x64.zip/ },
        { name: 'empty', assets: expected.map((name, index) => ({ name, size: index === 3 ? 0 : 10 })), error: /not uploaded or empty: RunPad-x86_64.AppImage/ },
        { name: 'unfinished', assets: expected.map((name, index) => ({ name, state: index === 0 ? 'starter' : 'uploaded' })), error: /not uploaded or empty/ },
        { name: 'duplicated', assets: [...expected, expected[2]].map((name) => ({ name })), error: /duplicated: RunPad-linux-x64.tar.gz/ },
        { name: 'unexpected', assets: [...expected, 'extra.txt'].map((name) => ({ name })), error: /unexpected: extra.txt/ },
        { name: 'nothing uploaded', assets: [], error: /missing/ },
    ];
    for (const testCase of cases)
    {
        const mock = makeGithub();
        const owned = mock.addRelease(ownedDraftFields(context));
        testCase.assets.forEach((asset) => mock.addAsset(owned.id, asset));
        await assert.rejects(
            helper.publishDraft({ github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION }),
            testCase.error,
            testCase.name);
        assert.ok(!callNames(mock.state).includes('updateRelease'), testCase.name);
        assert.equal(mock.state.releases.find((release) => release.id === owned.id).draft, true, testCase.name);
    }
});

test('publishDraft lists every asset page before deciding', async () =>
{
    const context = makeContext();
    const mock = makeGithub();
    const owned = mock.addRelease(ownedDraftFields(context));
    helper.expectedAssetNames(VERSION).forEach((name) => mock.addAsset(owned.id, { name }));

    await helper.publishDraft({ github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION });
    assert.equal(callNames(mock.state).filter((name) => name === 'listReleaseAssets').length, 4);
    assert.ok(callNames(mock.state).includes('updateRelease'));
});

test('publishDraft refuses foreign drafts and an already published version', async () =>
{
    const context = makeContext();
    const expected = helper.expectedAssetNames(VERSION);

    const foreign = makeGithub();
    const foreignDraft = foreign.addRelease(ownedDraftFields(makeContext({ runId: 1 })));
    expected.forEach((name) => foreign.addAsset(foreignDraft.id, { name }));
    await assert.rejects(
        helper.publishDraft({ github: foreign.github, context, core: makeCore(), releaseId: foreignDraft.id, version: VERSION }),
        /refusing to modify it/);

    const raced = makeGithub({ tags: [TAG] });
    const owned = raced.addRelease(ownedDraftFields(context));
    expected.forEach((name) => raced.addAsset(owned.id, { name }));
    await assert.rejects(
        helper.publishDraft({ github: raced.github, context, core: makeCore(), releaseId: owned.id, version: VERSION }),
        /already published/);

    for (const mock of [foreign, raced])
    {
        assert.ok(!callNames(mock.state).some((name) => /update|delete/.test(name)));
    }
});

test('API failures propagate instead of being swallowed', async () =>
{
    const context = makeContext();
    for (const method of ['listReleases', 'getRef', 'createRelease'])
    {
        const mock = makeGithub();
        mock.state.fail[method] = httpError(500, `${method} exploded`);
        await assert.rejects(
            helper.prepareDraft({ github: mock.github, context, core: makeCore(), version: VERSION }),
            new RegExp(`${method} exploded`));
    }

    for (const method of ['getRelease', 'listReleaseAssets', 'deleteReleaseAsset', 'uploadReleaseAsset'])
    {
        const mock = makeGithub();
        const owned = mock.addRelease(ownedDraftFields(context));
        mock.addAsset(owned.id, { name: 'RunPad-win-x64.zip' });
        mock.state.fail[method] = httpError(502, `${method} exploded`);
        await assert.rejects(
            helper.uploadAssets({
                github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION, files: makeFiles(PLATFORM_FILES[0]),
            }),
            new RegExp(`${method} exploded`));
    }

    for (const method of ['getRelease', 'listReleases', 'listReleaseAssets', 'updateRelease'])
    {
        const mock = makeGithub();
        const owned = mock.addRelease(ownedDraftFields(context));
        helper.expectedAssetNames(VERSION).forEach((name) => mock.addAsset(owned.id, { name }));
        mock.state.fail[method] = httpError(503, `${method} exploded`);
        await assert.rejects(
            helper.publishDraft({ github: mock.github, context, core: makeCore(), releaseId: owned.id, version: VERSION }),
            new RegExp(`${method} exploded`));
    }
});
