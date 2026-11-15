import * as React from "react";
import { Corner, Root, ScrollAreaScrollbar, ScrollAreaThumb, Viewport } from "@radix-ui/react-scroll-area";
import { cn } from "@/lib/utils";

function ScrollBar({
  className,
  orientation = "vertical",
  ...props
}: React.ComponentProps<typeof ScrollAreaScrollbar>) {
  return (
    <ScrollAreaScrollbar
      orientation={orientation}
      className={cn(
        "flex touch-none select-none transition-colors",
        orientation === "vertical" && "h-full w-2.5 border-l border-l-transparent p-px",
        orientation === "horizontal" && "h-2.5 flex-col border-t border-t-transparent p-px",
        className,
      )}
      {...props}
    >
      <ScrollAreaThumb className="relative flex-1 rounded-full bg-border" />
    </ScrollAreaScrollbar>
  );
}

// Children render inside the viewport, so both bars are built in here.
function ScrollArea({ className, children, ...props }: React.ComponentProps<typeof Root>) {
  return (
    <Root className={cn("relative overflow-hidden", className)} {...props}>
      <Viewport className="h-full w-full rounded-[inherit]">{children}</Viewport>
      <ScrollBar orientation="vertical" />
      <ScrollBar orientation="horizontal" />
      <Corner />
    </Root>
  );
}

export { ScrollArea, ScrollBar };
