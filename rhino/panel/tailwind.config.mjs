import animate from 'tailwindcss-animate';

/** @type {import('tailwindcss').Config} */
export default {
  darkMode: ['class'],
  plugins: [animate],
  content: ['./src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: Object.fromEntries([
        'background', 'foreground', 'border', 'input', 'ring',
        'primary', 'secondary', 'muted', 'accent', 'destructive', 'card', 'popover',
      ].map((name) => [name, ['primary', 'secondary', 'muted', 'accent', 'destructive', 'card', 'popover'].includes(name)
        ? { DEFAULT: `hsl(var(--${name}) / <alpha-value>)`, foreground: `hsl(var(--${name}-foreground) / <alpha-value>)` }
        : `hsl(var(--${name}) / <alpha-value>)`]).concat(
        // RhinoTrunk change states: bg-added-soft text-added, etc.
        ['added', 'modified', 'deleted', 'agent'].map((name) => [name, {
          DEFAULT: `hsl(var(--${name}) / <alpha-value>)`, soft: `hsl(var(--${name}-soft) / <alpha-value>)`,
        }]))),
      borderRadius: { lg: 'var(--radius)', md: 'calc(var(--radius) - 2px)', sm: 'calc(var(--radius) - 4px)' },
    },
  },
};
