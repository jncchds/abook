namespace ABook.Agents;

/// <summary>
/// JSON schemas for structured output from planning agents. Used with CreateJsonSchemaFormat
/// to constrain the LLM response shape and prevent type mismatches (e.g., object vs array).
/// </summary>
internal static class JsonSchemas
{
    public const string StoryBible = """
        {
          "type": "object",
          "properties": {
            "settingDescription": { "type": "string" },
            "timePeriod": { "type": "string" },
            "themes": { "type": "string" },
            "toneAndStyle": { "type": "string" },
            "worldRules": { "type": "string" },
            "notes": { "type": "string" }
          },
          "required": ["settingDescription", "timePeriod", "themes", "toneAndStyle", "worldRules", "notes"],
          "additionalProperties": false
        }
        """;

    public const string Characters = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "role": { "type": "string" },
              "physicalDescription": { "type": "string" },
              "personality": { "type": "string" },
              "backstory": { "type": "string" },
              "goalMotivation": { "type": "string" },
              "arc": { "type": "string" },
              "firstAppearanceChapterNumber": { "type": ["integer", "null"] },
              "notes": { "type": "string" }
            },
            "required": ["name", "role"],
            "additionalProperties": false
          }
        }
        """;

    public const string PlotThreads = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "type": { "type": "string" },
              "status": { "type": "string" },
              "description": { "type": "string" },
              "introducedChapterNumber": { "type": ["integer", "null"] },
              "relatedCharacters": { "type": "array", "items": { "type": "string" } },
              "resolution": { "type": "string" },
              "notes": { "type": "string" }
            },
            "required": ["name", "type"],
            "additionalProperties": false
          }
        }
        """;

    public const string ChapterOutlines = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "number": { "type": "integer" },
              "title": { "type": "string" },
              "outline": { "type": "string" },
              "pointOfViewCharacter": { "type": ["string", "null"] },
              "foreshadowingNotes": { "type": ["string", "null"] },
              "payoffNotes": { "type": ["string", "null"] }
            },
            "required": ["number", "title", "outline"],
            "additionalProperties": false
          }
        }
        """;
}
