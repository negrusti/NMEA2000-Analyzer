module.exports = async function cleanupReleases({ github, context, core, releaseId }) {
  const repo = context.repo;
  const published = (await github.paginate(github.rest.repos.listReleases, {
    ...repo,
    per_page: 100,
  }))
    .filter(release => !release.draft)
    .sort((a, b) => Date.parse(b.published_at) - Date.parse(a.published_at) || b.id - a.id);

  if (!published.some(release => String(release.id) === releaseId)) {
    throw new Error('The uploaded release was not found among published releases; nothing was deleted.');
  }

  const newest = published[0];
  const assets = await github.paginate(github.rest.repos.listReleaseAssets, {
    ...repo,
    release_id: newest.id,
    per_page: 100,
  });
  if (!assets.some(asset => asset.name === 'NMEA2000Analyzer.zip' && asset.state === 'uploaded' && asset.size > 0)) {
    throw new Error('The newest release has no complete application ZIP; nothing was deleted.');
  }

  core.info(`Keeping release ${newest.tag_name} and its downloads. Git tags are preserved.`);
  for (const release of published.slice(1)) {
    await github.rest.repos.deleteRelease({ ...repo, release_id: release.id });
    core.info(`Deleted old release ${release.tag_name}.`);
  }

  // These workflow artifacts duplicate the ZIP uploaded directly to Releases.
  const artifacts = await github.paginate(github.rest.actions.listArtifactsForRepo, {
    ...repo,
    name: 'NMEA2000 Analyzer',
    per_page: 100,
  });
  for (const artifact of artifacts.filter(artifact => artifact.name === 'NMEA2000 Analyzer')) {
    await github.rest.actions.deleteArtifact({ ...repo, artifact_id: artifact.id });
    core.info(`Deleted redundant build artifact ${artifact.id}.`);
  }
};
