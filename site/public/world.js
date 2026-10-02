// Nome do mundo (WORLD_NAME), que o servidor poe em <html data-world>.
export const WORLD = document.documentElement.dataset.world || 'Valheim';
export const storeKey = (name) => `${WORLD.toLowerCase()}.${name}`;
