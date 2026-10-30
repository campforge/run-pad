// Direct draft-release transfer for the CI workflow.
//
// Each platform job uploads its archives straight to one draft GitHub Release instead of handing
// them to the final job through Actions artifacts (the artifact storage quota is exhausted). The
// final job publishes the draft only after every platform job succeeded and all seven assets are
// present. Called from actions/github-script@v7; github/context/core are passed in so the logic can
// be tested against a mocked REST API (draft-release.test.js).
'use strict';

const fs = require('fs');
const path = require('path');

function releaseTag(version)
{
    return `v${version}`;
}

function expectedAssetNames(version)
{
    return [
        'Invoklet-win-x64.zip',
        'Invoklet-win-x64.msi',
        'Invoklet-linux-x64.tar.gz',
        'Invoklet-x86_64.AppImage',
        `invoklet_${version}_amd64.deb`,
        'Invoklet-osx-x64.dmg',
        'Invoklet-osx-arm64.dmg',
    ];
}

// Ties a draft to the commit and workflow run that created it, so a retry of the same run reuses
// it and a different run/commit never mixes its builds into it.
function ownershipMarker(context)
{
    return `<!-- invoklet-release source-sha=${context.sha} run-id=${context.runId} -->`;
}

// Portable replacement for `grep -oP` (not available in macOS grep).
function readVersion(csprojText)
{
    const match = /<Version>([^<]*)<\/Version>/.exec(csprojText);
    if (!match)
    {
        throw new Error('No <Version> element found in Invoklet.csproj.');
    }
    const version = match[1].trim();
    if (!/^[0-9A-Za-z][0-9A-Za-z.+-]*$/.test(version))
    {
        throw new Error(`Unexpected <Version> value '${version}' in Invoklet.csproj.`);
    }
    return version;
}

function readVersionFromFile(csprojPath)
{
    return readVersion(fs.readFileSync(csprojPath, 'utf8'));
}

// CHANGELOG.md is wrapped at ~100 columns, but a release body renders every newline as a line
// break; joins each continuation line onto the paragraph or list item it continues.
function unwrapLines(lines)
{
    const result = [];
    let inFence = false;
    for (const line of lines)
    {
        const previous = result.length > 0 ? result[result.length - 1] : '';
        const startsBlock = /^\s*([-*+] |\d+\. |#|>|\||```)/.test(line);
        if (!inFence && line.trim() && previous.trim() && !startsBlock && !/^\s*(#|```)/.test(previous))
        {
            result[result.length - 1] = `${previous} ${line.trim()}`;
        }
        else
        {
            result.push(line);
        }
        if (/^\s*```/.test(line))
        {
            inFence = !inFence;
        }
    }
    return result;
}

// The release notes are the version's "## [x.y.z]" section of CHANGELOG.md, without its heading,
// up to the next version or the link definitions at the end of the file.
function readReleaseNotes(changelogText, version)
{
    const lines = changelogText.replace(/\r\n/g, '\n').split('\n');
    const escaped = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const heading = new RegExp(`^## \\[?${escaped}\\]?(\\s|$)`);
    const start = lines.findIndex((line) => heading.test(line));
    if (start < 0)
    {
        throw new Error(
            `CHANGELOG.md has no "## [${version}]" section. Describe what changed in ${version} there ` +
            'before bumping <Version>; it becomes the release notes.');
    }
    const section = [];
    for (const line of lines.slice(start + 1))
    {
        if (/^## /.test(line) || /^\[[^\]]+\]:\s/.test(line))
        {
            break;
        }
        section.push(line);
    }
    const notes = unwrapLines(section).join('\n').trim();
    if (!notes)
    {
        throw new Error(`The "## [${version}]" section of CHANGELOG.md is empty.`);
    }
    return notes;
}

function readReleaseNotesFromFile(changelogPath, version)
{
    return readReleaseNotes(fs.readFileSync(changelogPath, 'utf8'), version);
}

// Links relative to the repository root (ROADMAP.md, src/...) do not resolve in a release body;
// point them at the released commit instead.
function absolutizeLinks(notes, context)
{
    const { owner, repo } = context.repo;
    return notes.replace(/\]\((?![a-z][a-z0-9+.-]*:|#|\/)([^)\s]+)\)/gi,
        (_, target) => `](https://github.com/${owner}/${repo}/blob/${context.sha}/${target})`);
}

function isNotFound(error)
{
    return error && error.status === 404;
}

async function tagExists(github, context, tag)
{
    const { owner, repo } = context.repo;
    try
    {
        await github.rest.git.getRef({ owner, repo, ref: `tags/${tag}` });
        return true;
    }
    catch (error)
    {
        if (isNotFound(error))
        {
            return false;
        }
        throw error;
    }
}

function versionPublishedError(version)
{
    return new Error(
        `Invoklet v${version} is already published (release or tag ${releaseTag(version)} exists). ` +
        'Bump <Version> in src/main/Invoklet/Invoklet.csproj to release this commit; ' +
        'published releases are never replaced.');
}

// Fails if the version is already visible: a published release with this tag, or the tag itself
// (a draft does not create its tag until it is published).
async function assertVersionUnpublished(github, context, version, releases)
{
    const tag = releaseTag(version);
    if (releases.some((release) => !release.draft && release.tag_name === tag))
    {
        throw versionPublishedError(version);
    }
    if (await tagExists(github, context, tag))
    {
        throw versionPublishedError(version);
    }
}

async function listReleases(github, context)
{
    const { owner, repo } = context.repo;
    return github.paginate(github.rest.repos.listReleases, { owner, repo, per_page: 100 });
}

function isOwnedDraft(release, context, version)
{
    return release.draft === true
        && release.tag_name === releaseTag(version)
        && release.target_commitish === context.sha
        && typeof release.body === 'string'
        && release.body.includes(ownershipMarker(context));
}

async function getOwnedDraft(github, context, releaseId, version)
{
    const id = Number(releaseId);
    if (!Number.isInteger(id) || id <= 0)
    {
        throw new Error(`Invalid draft release id '${releaseId}'.`);
    }
    if (!version)
    {
        throw new Error('Release version is missing.');
    }
    const { owner, repo } = context.repo;
    const { data: release } = await github.rest.repos.getRelease({ owner, repo, release_id: id });
    if (!isOwnedDraft(release, context, version))
    {
        throw new Error(
            `Release ${id} is not the draft ${releaseTag(version)} owned by commit ${context.sha} ` +
            `and run ${context.runId}; refusing to modify it.`);
    }
    return release;
}

// Creates the draft for this run, with `notes` (Markdown) as its description, or reuses it when the
// same run is retried.
async function prepareDraft({ github, context, core, version, notes })
{
    const { owner, repo } = context.repo;
    const tag = releaseTag(version);
    const releases = await listReleases(github, context);
    await assertVersionUnpublished(github, context, version, releases);

    const drafts = releases.filter((release) => release.draft && release.tag_name === tag);
    const owned = drafts.filter((release) => isOwnedDraft(release, context, version));
    const foreign = drafts.filter((release) => !isOwnedDraft(release, context, version));
    if (foreign.length > 0)
    {
        throw new Error(
            `Draft release(s) ${foreign.map((release) => release.id).join(', ')} for ${tag} belong to ` +
            `another commit or workflow run. Delete the stale draft(s) or bump <Version>; ` +
            'builds from different runs are never mixed.');
    }
    if (owned.length > 1)
    {
        throw new Error(`Several drafts for ${tag} are owned by run ${context.runId}: ` +
            `${owned.map((release) => release.id).join(', ')}.`);
    }
    if (owned.length === 1)
    {
        core.info(`Reusing draft release ${owned[0].id} for ${tag}.`);
        return owned[0];
    }

    const { data: created } = await github.rest.repos.createRelease({
        owner,
        repo,
        tag_name: tag,
        target_commitish: context.sha,
        name: `Invoklet ${tag}`,
        body: notes ? `${absolutizeLinks(notes, context)}\n\n${ownershipMarker(context)}` : ownershipMarker(context),
        draft: true,
    });
    core.info(`Created draft release ${created.id} for ${tag}.`);
    return created;
}

// Uploads local files to the owned draft, replacing only same-name assets of that draft (a retry of
// a failed job); assets uploaded by other, already successful jobs are left alone.
async function uploadAssets({ github, context, core, releaseId, version, files })
{
    const { owner, repo } = context.repo;
    const release = await getOwnedDraft(github, context, releaseId, version);

    const uploads = files.map((file) =>
    {
        if (!fs.existsSync(file))
        {
            throw new Error(`Release file '${file}' does not exist.`);
        }
        const size = fs.statSync(file).size;
        if (size <= 0)
        {
            throw new Error(`Release file '${file}' is empty.`);
        }
        return { file, name: path.basename(file), size };
    });

    // Collect every page first so deleting doesn't shift later pages.
    const existing = await github.paginate(github.rest.repos.listReleaseAssets, {
        owner,
        repo,
        release_id: release.id,
        per_page: 100,
    });

    for (const upload of uploads)
    {
        for (const asset of existing.filter((candidate) => candidate.name === upload.name))
        {
            core.info(`Replacing ${asset.name} (${asset.id}) in draft ${release.id}.`);
            await github.rest.repos.deleteReleaseAsset({ owner, repo, asset_id: asset.id });
        }

        const { data: asset } = await github.rest.repos.uploadReleaseAsset({
            owner,
            repo,
            release_id: release.id,
            name: upload.name,
            data: fs.readFileSync(upload.file),
            headers: {
                'content-type': 'application/octet-stream',
                'content-length': upload.size,
            },
        });
        if (!asset || asset.state !== 'uploaded' || asset.size !== upload.size)
        {
            throw new Error(
                `Upload of ${upload.name} to draft ${release.id} is incomplete ` +
                `(state ${asset && asset.state}, size ${asset && asset.size}, expected ${upload.size}).`);
        }
        core.info(`Uploaded ${upload.name} (${upload.size} bytes) to draft ${release.id}.`);
    }
}

// Publishes the owned draft once it holds exactly the seven expected, fully uploaded assets.
async function publishDraft({ github, context, core, releaseId, version })
{
    const { owner, repo } = context.repo;
    const release = await getOwnedDraft(github, context, releaseId, version);
    await assertVersionUnpublished(github, context, version, await listReleases(github, context));

    const assets = await github.paginate(github.rest.repos.listReleaseAssets, {
        owner,
        repo,
        release_id: release.id,
        per_page: 100,
    });
    const expected = expectedAssetNames(version);
    const names = assets.map((asset) => asset.name);
    const missing = expected.filter((name) => !names.includes(name));
    const unexpected = names.filter((name) => !expected.includes(name));
    const duplicated = expected.filter((name) => names.filter((other) => other === name).length > 1);
    const incomplete = assets
        .filter((asset) => asset.state !== 'uploaded' || !(asset.size > 0))
        .map((asset) => asset.name);
    const problems = [];
    if (missing.length > 0)
    {
        problems.push(`missing: ${missing.join(', ')}`);
    }
    if (unexpected.length > 0)
    {
        problems.push(`unexpected: ${unexpected.join(', ')}`);
    }
    if (duplicated.length > 0)
    {
        problems.push(`duplicated: ${duplicated.join(', ')}`);
    }
    if (incomplete.length > 0)
    {
        problems.push(`not uploaded or empty: ${incomplete.join(', ')}`);
    }
    if (problems.length > 0)
    {
        throw new Error(`Draft ${release.id} is not ready to publish (${problems.join('; ')}). ` +
            'The draft is kept for a retry.');
    }

    await github.rest.repos.updateRelease({
        owner,
        repo,
        release_id: release.id,
        tag_name: releaseTag(version),
        target_commitish: context.sha,
        draft: false,
    });
    core.info(`Published Invoklet ${releaseTag(version)} (release ${release.id}).`);
}

module.exports = {
    absolutizeLinks,
    expectedAssetNames,
    ownershipMarker,
    publishDraft,
    prepareDraft,
    readReleaseNotes,
    readReleaseNotesFromFile,
    readVersion,
    readVersionFromFile,
    releaseTag,
    uploadAssets,
};
