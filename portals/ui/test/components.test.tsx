/**
 * The primitives, checked against the two rules `@mageride/ui` holds itself to:
 * the CTA is the D2 CTA token, and no component carries user-facing text of its
 * own.
 *
 * Everything is asserted through the rendered DOM rather than through the class
 * constants, because a class the component never emits is not a style.
 */

import { CTA, CTA_CLASS_NAMES, SEMANTIC_COLORS, VEHICLE_COLORS } from '@mageride/tailwind-preset';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  Button,
  Chip,
  Dropzone,
  Field,
  Input,
  Modal,
  StatusPill,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
  Tabs,
  cx,
} from '../src/index.js';

afterEach(cleanup);

describe('Button — the D2 CTA token', () => {
  it('emits every class the CTA token declares', () => {
    render(<Button>{'ok'}</Button>);
    const classes = new Set(screen.getByRole('button').className.split(/\s+/));

    for (const expected of CTA_CLASS_NAMES.split(' ')) {
      expect(classes.has(expected), `CTA class ${expected} missing`).toBe(true);
    }
  });

  it('declares the CTA class list from the D2 token values, not from literals', () => {
    // Guards the constant itself: if D2's height, radius, colours or label role
    // changed, `CTA_CLASS_NAMES` has to change with them.
    expect(CTA_CLASS_NAMES).toContain('h-cta');
    expect(CTA_CLASS_NAMES).toContain('rounded-sm');
    expect(CTA_CLASS_NAMES).toContain(`bg-${CTA.background}`);
    expect(CTA_CLASS_NAMES).toContain(`text-${CTA.label}`);
    expect(CTA_CLASS_NAMES).toContain(`text-${CTA.labelRole}`);
  });

  it('is a non-submitting button unless asked otherwise', () => {
    // A bare <button> inside a form submits it. That surprise belongs to the
    // caller, not to every screen that puts a CTA in a form.
    render(<Button>{'ok'}</Button>);
    expect(screen.getByRole('button')).toHaveProperty('type', 'button');
  });

  it('disables itself and announces the busy state while loading', () => {
    render(
      <Button busy busyLabel="…">
        {'ok'}
      </Button>,
    );
    const button = screen.getByRole('button');
    expect(button).toHaveProperty('disabled', true);
    expect(button.getAttribute('aria-busy')).toBe('true');
  });

  it('keeps the label in place while busy so the button does not resize', () => {
    render(
      <Button busy busyLabel="…">
        {'Confirm'}
      </Button>,
    );
    expect(screen.getByRole('button').textContent).toContain('Confirm');
  });

  it("lets the caller's className win a conflict", () => {
    render(<Button className="bg-error">{'ok'}</Button>);
    const classes = screen.getByRole('button').className;
    expect(classes).toContain('bg-error');
    expect(classes).not.toContain('bg-primary ');
  });

  it('drops the compact size to 40px — ten units of the 4px grid', () => {
    render(<Button size="compact">{'ok'}</Button>);
    expect(screen.getByRole('button').className).toContain('h-10');
  });
});

describe('Field', () => {
  it('wires the label, the hint and the error to the control', () => {
    render(
      <Field label="Plate number" hint="Rear plate" error="Not a valid plate">
        <Input />
      </Field>,
    );

    const input = screen.getByLabelText('Plate number');
    const describedBy = input.getAttribute('aria-describedby')?.split(' ') ?? [];

    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(describedBy).toHaveLength(2);
    for (const id of describedBy) {
      expect(document.getElementById(id)?.textContent).toBeTruthy();
    }
  });

  it('announces the error the moment it appears', () => {
    render(
      <Field label="Plate number" error="Not a valid plate">
        <Input />
      </Field>,
    );
    expect(screen.getByRole('alert').textContent).toBe('Not a valid plate');
  });

  it('leaves a valid control unmarked', () => {
    render(
      <Field label="Plate number">
        <Input />
      </Field>,
    );
    expect(screen.getByLabelText('Plate number').getAttribute('aria-invalid')).toBeNull();
  });

  it('gives each field its own ids', () => {
    render(
      <>
        <Field label="One">
          <Input />
        </Field>
        <Field label="Two">
          <Input />
        </Field>
      </>,
    );
    const [first, second] = screen.getAllByRole('textbox');
    expect(first?.id).not.toBe(second?.id);
  });
});

describe('Chip', () => {
  it('is a toggle, not a button that happens to look pressed', () => {
    render(<Chip selected>{'Sedan'}</Chip>);
    expect(screen.getByRole('button').getAttribute('aria-pressed')).toBe('true');
  });

  it('paints the accent dot with the D2 vehicle-type hex', () => {
    render(<Chip accent="veh-tuk">{'Tuk'}</Chip>);
    const style = screen.getByRole('button').getAttribute('style') ?? '';
    expect(style.toLowerCase()).toContain(VEHICLE_COLORS['veh-tuk'].hex.toLowerCase());
  });

  it('leaves the accent off when none is given', () => {
    render(<Chip>{'All'}</Chip>);
    expect(screen.getByRole('button').getAttribute('style')).toBeNull();
  });
});

describe('StatusPill', () => {
  it.each(['neutral', 'info', 'success', 'warning', 'error'] as const)(
    '%s renders its label',
    (tone) => {
      render(<StatusPill tone={tone}>{'Pending'}</StatusPill>);
      expect(screen.getByText('Pending')).toBeTruthy();
    },
  );

  it('builds its tones from D2 semantic roles only', () => {
    render(<StatusPill tone="success">{'Approved'}</StatusPill>);
    const classes = screen.getByText('Approved').className;
    expect(classes).toContain('text-success');
    expect(SEMANTIC_COLORS.success.light).toBe('#2E9E4F');
  });
});

describe('Table', () => {
  it('names the table without necessarily showing the caption', () => {
    render(
      <Table caption="Verification queue">
        <THead>
          <TR>
            <TH>{'Driver'}</TH>
          </TR>
        </THead>
        <TBody>
          <TR>
            <TD>{'A'}</TD>
          </TR>
        </TBody>
      </Table>,
    );
    expect(screen.getByRole('table', { name: 'Verification queue' })).toBeTruthy();
    expect(screen.getByText('Verification queue').className).toContain('sr-only');
  });

  it('scrolls sideways rather than making the page scroll', () => {
    const { container } = render(
      <Table caption="Wide">
        <TBody>
          <TR>
            <TD>{'A'}</TD>
          </TR>
        </TBody>
      </Table>,
    );
    expect(container.firstElementChild?.className).toContain('overflow-x-auto');
  });

  it('spans the empty row across the whole table', () => {
    render(
      <Table caption="Empty">
        <TBody>
          <TableEmpty colSpan={4}>{'Nothing to show'}</TableEmpty>
        </TBody>
      </Table>,
    );
    expect(screen.getByText('Nothing to show').getAttribute('colspan')).toBe('4');
  });
});

describe('Modal', () => {
  it('names the dialog and its close control', () => {
    render(
      <Modal open onOpenChange={() => {}} title="Activate feed" closeLabel="Close dialog">
        {'body'}
      </Modal>,
    );
    expect(screen.getByRole('dialog', { name: 'Activate feed' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Close dialog' })).toBeTruthy();
  });

  it('renders nothing while closed', () => {
    render(
      <Modal open={false} onOpenChange={() => {}} title="Activate feed" closeLabel="Close">
        {'body'}
      </Modal>,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('reports a close request rather than closing itself', () => {
    // Open state stays the caller's — a modal that closed itself would fight
    // any screen that needs to confirm before dismissing.
    const onOpenChange = vi.fn();
    render(
      <Modal open onOpenChange={onOpenChange} title="Activate feed" closeLabel="Close">
        {'body'}
      </Modal>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});

describe('Tabs', () => {
  it('names the tab list and shows the first panel', () => {
    render(
      <Tabs
        label="Finance sections"
        items={[
          { value: 'wallets', label: 'Wallets', content: 'wallet rows' },
          { value: 'credits', label: 'Credits', content: 'credit rows' },
        ]}
      />,
    );
    expect(screen.getByRole('tablist', { name: 'Finance sections' })).toBeTruthy();
    expect(screen.getByText('wallet rows')).toBeTruthy();
    expect(screen.queryByText('credit rows')).toBeNull();
  });

  it('switches panels on click', () => {
    render(
      <Tabs
        label="Finance sections"
        items={[
          { value: 'wallets', label: 'Wallets', content: 'wallet rows' },
          { value: 'credits', label: 'Credits', content: 'credit rows' },
        ]}
      />,
    );
    // Radix activates a tab on pointer-down, not on click — that is what makes
    // a tab feel immediate rather than lagging behind the mouse release.
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Credits' }));
    expect(screen.getByText('credit rows')).toBeTruthy();
  });
});

describe('Dropzone', () => {
  function file(name: string, type: string, size: number): File {
    const f = new File(['x'], name, { type });
    Object.defineProperty(f, 'size', { value: size });
    return f;
  }

  it('keeps a real file input under the label', () => {
    render(<Dropzone label="Upload the GTFS zip" accept=".zip" onFiles={() => {}} />);
    const input = screen.getByLabelText(/Upload the GTFS zip/);
    expect(input).toHaveProperty('type', 'file');
    expect(input.getAttribute('accept')).toBe('.zip');
  });

  it('accepts a file that matches the extension and the size limit', () => {
    const onFiles = vi.fn();
    render(
      <Dropzone
        label="Upload"
        accept=".zip"
        maxSizeBytes={200 * 1024 * 1024}
        onFiles={onFiles}
      />,
    );
    const good = file('feed.zip', 'application/zip', 1024);
    fireEvent.change(screen.getByLabelText('Upload'), { target: { files: [good] } });
    expect(onFiles).toHaveBeenCalledWith([good]);
  });

  it('rejects the wrong type and the oversized file, with the reason', () => {
    // SCR-AP-016: ".zip only, ≤ 200 MB". The check here saves a 200 MB upload;
    // the server still has to make the same one.
    const onFiles = vi.fn();
    const onReject = vi.fn();
    render(
      <Dropzone
        label="Upload"
        accept=".zip"
        maxSizeBytes={200 * 1024 * 1024}
        multiple
        onFiles={onFiles}
        onReject={onReject}
      />,
    );

    const wrongType = file('feed.csv', 'text/csv', 10);
    const tooBig = file('huge.zip', 'application/zip', 201 * 1024 * 1024);
    fireEvent.change(screen.getByLabelText('Upload'), { target: { files: [wrongType, tooBig] } });

    expect(onFiles).not.toHaveBeenCalled();
    expect(onReject).toHaveBeenCalledWith([
      { reason: 'type', file: wrongType },
      { reason: 'size', file: tooBig },
    ]);
  });

  it('rejects extra files when it is single-file', () => {
    const onFiles = vi.fn();
    const onReject = vi.fn();
    render(<Dropzone label="Upload" onFiles={onFiles} onReject={onReject} />);

    const first = file('a.zip', 'application/zip', 1);
    const second = file('b.zip', 'application/zip', 1);
    fireEvent.change(screen.getByLabelText('Upload'), { target: { files: [first, second] } });

    expect(onFiles).toHaveBeenCalledWith([first]);
    expect(onReject).toHaveBeenCalledWith([{ reason: 'count', file: second }]);
  });

  it('clears the input so the same file can be retried', () => {
    // Without this a failed upload cannot be retried with the same file: the
    // browser fires no `change` event when the value has not changed.
    const onFiles = vi.fn();
    render(<Dropzone label="Upload" onFiles={onFiles} />);
    const input = screen.getByLabelText('Upload') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file('a.zip', 'application/zip', 1)] } });
    expect(input.value).toBe('');
  });
});

describe('cx', () => {
  it('resolves a conflict in favour of the last class', () => {
    expect(cx('bg-primary', 'bg-error')).toBe('bg-error');
    expect(cx('p-md', 'p-lg')).toBe('p-lg');
    expect(cx('rounded-sm', 'rounded-card')).toBe('rounded-card');
    expect(cx('text-body', 'text-title')).toBe('text-title');
    expect(cx('shadow-elevation-1', 'shadow-elevation-5')).toBe('shadow-elevation-5');
  });

  it('understands MageRide tokens, not just Tailwind\'s own scales', () => {
    // The whole reason `cx` teaches tailwind-merge the preset: without the
    // theme extension these read as unrelated classes and both survive.
    expect(cx('bg-veh-sedan', 'bg-veh-tuk')).toBe('bg-veh-tuk');
    expect(cx('text-on-surface', 'text-on-surface-variant')).toBe('text-on-surface-variant');
    expect(cx('font-display', 'font-body')).toBe('font-body');
  });

  it('keeps classes that do not conflict', () => {
    expect(cx('bg-primary', 'text-on-primary', false, null, 'rounded-sm')).toBe(
      'bg-primary text-on-primary rounded-sm',
    );
  });
});
