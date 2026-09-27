import { describe, expect, test } from "vite-plus/test";
import { WorkItem } from "../../types";
import {
  EMPTY_TASKBOARD_FILTER,
  compareTags,
  matchesTaskboardFilter,
  sameTag,
} from "../taskboardFilter";

function item(overrides: Partial<WorkItem>): WorkItem {
  return { id: "card-1", title: "", description: "", tags: [], ...overrides } as WorkItem;
}

describe("the board folds case one character at a time, as the server's OrdinalIgnoreCase", () => {
  test("a letter whose upper case is several letters is not those letters", () => {
    expect(sameTag("ß", "SS")).toBe(false);
    expect(sameTag("Straße", "STRASSE")).toBe(false);
    expect(sameTag("Straße", "STRAßE")).toBe(true);
    expect(["SS", "ß", "ss"].sort(compareTags)).toEqual(["SS", "ss", "ß"]);
  });

  test("dotless ı and long ſ keep their own case, as .NET keeps them", () => {
    expect(sameTag("ı", "I")).toBe(false);
    expect(sameTag("ſ", "S")).toBe(false);
    expect(sameTag("ı", "ı")).toBe(true);
    expect(
      matchesTaskboardFilter(item({ title: "ıZMAT" }), { ...EMPTY_TASKBOARD_FILTER, search: "I" }),
    ).toBe(false);
    expect(
      matchesTaskboardFilter(item({ tags: ["ſ"] }), { ...EMPTY_TASKBOARD_FILTER, tags: ["S"] }),
    ).toBe(false);
  });

  test("a Greek letter with iota subscript is its titlecase partner", () => {
    expect(sameTag("ᾀ", "ᾈ")).toBe(true);
    expect(sameTag("ᾳ", "ᾼ")).toBe(true);
    expect(sameTag("ᾀ", "ἈΙ")).toBe(false);
    expect(
      matchesTaskboardFilter(item({ title: "ᾠδή" }), { ...EMPTY_TASKBOARD_FILTER, search: "ᾨ" }),
    ).toBe(true);
    expect(
      matchesTaskboardFilter(item({ tags: ["ῳ"] }), { ...EMPTY_TASKBOARD_FILTER, tags: ["ῼ"] }),
    ).toBe(true);
  });

  test("a card carrying ß does not match a filter for SS", () => {
    expect(
      matchesTaskboardFilter(item({ tags: ["ß"] }), { ...EMPTY_TASKBOARD_FILTER, tags: ["SS"] }),
    ).toBe(false);
    expect(
      matchesTaskboardFilter(item({ tags: ["ß"] }), { ...EMPTY_TASKBOARD_FILTER, tags: ["ß"] }),
    ).toBe(true);
  });

  test("search does not match through a letter whose lower case is several letters", () => {
    // Lower-cased, "İ" is "i" plus a combining dot, so "i" would be found in it.
    const dotted = item({ title: "İZMAT" });
    expect(matchesTaskboardFilter(dotted, { ...EMPTY_TASKBOARD_FILTER, search: "i" })).toBe(false);
    expect(matchesTaskboardFilter(dotted, { ...EMPTY_TASKBOARD_FILTER, search: "İzmat" })).toBe(
      true,
    );
    expect(
      matchesTaskboardFilter(item({ title: "Grüße" }), {
        ...EMPTY_TASKBOARD_FILTER,
        search: "GRÜSSE",
      }),
    ).toBe(false);
  });
});
